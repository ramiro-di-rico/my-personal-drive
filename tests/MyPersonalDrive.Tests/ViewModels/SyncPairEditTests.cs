using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>Editing an existing pair's direction/conflict policy without recreating it.</summary>
public class SyncPairEditTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-edit").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-edit-{Guid.NewGuid():N}.db");
    private const string RemoteRoot = "/my-files/Docs";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_localRoot, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(SyncPairViewModel Row, SyncStateStore Store)> BuildAsync()
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath(RemoteRoot, "[]");

        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(cli);
        var provider = new ProtonDriveProvider(service);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        await store.CreatePairAsync(RemoteRoot, _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        await panel.InitializeAsync();
        return (Assert.Single(panel.Pairs), store);
    }

    [Fact]
    public async Task WithNoHandlerAttached_EditingIsUnavailable()
    {
        var (row, store) = await BuildAsync();

        await row.EditCommand.ExecuteAsync();

        Assert.Equal("Editar un par no está disponible.", row.StatusText);
        Assert.Equal(SyncDirection.RemoteToLocal, Assert.Single(await store.GetPairsAsync()).Direction);
    }

    [Fact]
    public async Task Saving_PersistsTheNewDirectionAndPolicy_AndUpdatesTheRow()
    {
        var (row, store) = await BuildAsync();
        row.RequestEditAsync = _ => Task.FromResult<EditSyncPairRequest?>(new EditSyncPairRequest(SyncDirection.TwoWay, ConflictPolicy.PreferRemote));

        await row.EditCommand.ExecuteAsync();

        Assert.Equal(SyncDirection.TwoWay, row.Direction);
        Assert.Equal(ConflictPolicy.PreferRemote, row.ConflictPolicy);
        Assert.Equal("Bidireccional", row.DirectionText);
        var persisted = Assert.Single(await store.GetPairsAsync());
        Assert.Equal(SyncDirection.TwoWay, persisted.Direction);
        Assert.Equal(ConflictPolicy.PreferRemote, persisted.ConflictPolicy);
    }

    [Fact]
    public async Task CancelingTheDialog_ChangesNothing()
    {
        var (row, store) = await BuildAsync();
        row.RequestEditAsync = _ => Task.FromResult<EditSyncPairRequest?>(null);

        await row.EditCommand.ExecuteAsync();

        Assert.Equal(SyncDirection.RemoteToLocal, row.Direction);
        Assert.Equal(SyncDirection.RemoteToLocal, Assert.Single(await store.GetPairsAsync()).Direction);
    }

    [Fact]
    public async Task RequestEditAsync_IsHandedTheRowItself()
    {
        var (row, _) = await BuildAsync();
        SyncPairViewModel? received = null;
        row.RequestEditAsync = r =>
        {
            received = r;
            return Task.FromResult<EditSyncPairRequest?>(null);
        };

        await row.EditCommand.ExecuteAsync();

        Assert.Same(row, received);
    }

    /// <summary>
    /// docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2: switching a pair to a direction that would
    /// start writing into a local folder another pair already uploads from (SyncPairValidator's
    /// real, correct refusal) looked indistinguishable from a silently-failed save — the dialog
    /// closed as if it worked, and only a StatusMessage line, easy to miss, actually explained why
    /// nothing changed. This pins down that the rejection also reaches RequestAlertAsync.
    /// </summary>
    [Fact]
    public async Task RejectingAnUnsafeDirectionChange_AlsoRaisesABlockingAlert_NotJustStatusText()
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath(RemoteRoot, "[]");
        var store = new SyncStateStore(_dbPath);
        var provider = new ProtonDriveProvider(new ProtonDriveService(cli));
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        // Two pairs sharing one local folder, both upload-only — the one safe overlap shape
        // (SyncPairValidator.FindOverlap's own doc comment) — so switching either to TwoWay would
        // let it start writing into what the other uploads.
        await store.CreatePairAsync(RemoteRoot, _localRoot, SyncDirection.LocalToRemote, ConflictPolicy.Ask);
        await store.CreatePairAsync("/my-files/Other", _localRoot, SyncDirection.LocalToRemote, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        await panel.InitializeAsync();
        var alerts = new List<string>();
        panel.RequestAlertAsync = message => { alerts.Add(message); return Task.CompletedTask; };
        var row = panel.Pairs.First(p => p.RemotePath == RemoteRoot);
        row.RequestEditAsync = _ => Task.FromResult<EditSyncPairRequest?>(new EditSyncPairRequest(SyncDirection.TwoWay, ConflictPolicy.Ask));

        await row.EditCommand.ExecuteAsync();

        Assert.Equal(SyncDirection.LocalToRemote, row.Direction);
        Assert.Contains("también está sincronizada (solo subida)", panel.StatusMessage);
        Assert.Contains(alerts, message => message.Contains("también está sincronizada (solo subida)"));
    }
}
