using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The add-pair flow at the view-model level. <see cref="SyncPairValidatorTests"/> covers the rules
/// exhaustively; what's proven here is the wiring — that the panel actually consults them, against
/// the pairs in the database rather than whatever happens to be loaded in the list.
/// </summary>
public class SyncPanelPairCreationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-pair-creation").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-pair-creation-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private (SyncPanelViewModel Panel, SyncStateStore Store) Build()
    {
        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(new FakeCliExecutor());
        var executor = new SyncExecutor(service, store, new LocalScanner(), new RemoteScanner(service));
        return (new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store)), store);
    }

    private static void Answer(SyncPanelViewModel panel, string remotePath, string localPath)
        => panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest(remotePath, localPath, SyncDirection.TwoWay, ConflictPolicy.Ask));

    [Fact]
    public async Task ANestedLocalFolder_IsRefused_AndNothingIsPersisted()
    {
        var (panel, store) = Build();
        var outer = Path.Combine(_root, "Docs");
        var inner = Path.Combine(outer, "Sub");
        Directory.CreateDirectory(inner);

        Answer(panel, "/my-files/Docs", outer);
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Single(panel.Pairs);

        Answer(panel, "/my-files/Other", inner);
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(panel.Pairs);
        Assert.Single(await store.GetPairsAsync());
        Assert.Contains("overlaps", panel.StatusMessage);
    }

    [Fact]
    public async Task ANestedRemoteFolder_IsRefused()
    {
        var (panel, store) = Build();
        Answer(panel, "/my-files/Docs", Path.Combine(_root, "A"));
        await panel.AddPairCommand.ExecuteAsync();

        Answer(panel, "/my-files/Docs/Sub", Path.Combine(_root, "B"));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(await store.GetPairsAsync());
        Assert.Contains("remote folder overlaps", panel.StatusMessage);
    }

    [Fact]
    public async Task ValidationSeesPairsAddedBehindThePanelsBack()
    {
        // The scheduler and other windows share the database; a panel that only checked its own
        // ObservableCollection would happily create an overlapping pair.
        var (panel, store) = Build();
        var outer = Path.Combine(_root, "Docs");
        await store.CreatePairAsync("/my-files/Docs", outer, SyncDirection.TwoWay, ConflictPolicy.Ask);
        Assert.Empty(panel.Pairs); // never loaded

        Answer(panel, "/my-files/Other", Path.Combine(outer, "Sub"));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Empty(panel.Pairs);
        Assert.Single(await store.GetPairsAsync());
        Assert.Contains("overlaps", panel.StatusMessage);
    }

    [Fact]
    public async Task TwoUnrelatedPairs_AreBothCreated()
    {
        var (panel, store) = Build();

        Answer(panel, "/my-files/A", Path.Combine(_root, "A"));
        await panel.AddPairCommand.ExecuteAsync();
        Answer(panel, "/my-files/B", Path.Combine(_root, "B"));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Equal(2, panel.Pairs.Count);
        Assert.Equal(2, (await store.GetPairsAsync()).Count);
    }

    [Fact]
    public async Task CancellingTheDialog_ChangesNothing()
    {
        var (panel, store) = Build();
        panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(null);

        await panel.AddPairCommand.ExecuteAsync();

        Assert.Empty(panel.Pairs);
        Assert.Empty(await store.GetPairsAsync());
    }
}
