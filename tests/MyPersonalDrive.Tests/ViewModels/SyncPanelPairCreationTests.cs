using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
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
        var provider = new ProtonDriveProvider(service);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        return (new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store)), store);
    }

    private static void Answer(SyncPanelViewModel panel, string remotePath, string localPath)
        => panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest(remotePath, localPath, SyncDirection.TwoWay, ConflictPolicy.Ask));

    [Fact]
    public async Task AddPairAsync_ForwardsItsPrefillToTheRequester()
    {
        var (panel, _) = Build();
        var outer = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(outer);
        SyncPairPrefill? received = null;
        panel.RequestNewPairAsync = prefill =>
        {
            received = prefill;
            return Task.FromResult<NewSyncPairRequest?>(new NewSyncPairRequest("/my-files/Docs", outer, SyncDirection.TwoWay, ConflictPolicy.Ask));
        };

        // "Sync Selected Path..." on a cloud row (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6) only
        // knows the remote side — the local side is still the user's to fill in via the dialog.
        await panel.AddPairAsync(new SyncPairPrefill("/my-files/Docs", null));

        Assert.Equal(new SyncPairPrefill("/my-files/Docs", null), received);
    }

    [Fact]
    public async Task FindPairByRemotePathAndLocalPath_ReturnTheMatchingRow_OrNull()
    {
        var (panel, _) = Build();
        var outer = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(outer);
        Answer(panel, "/my-files/Docs", outer);

        await panel.AddPairCommand.ExecuteAsync();

        Assert.NotNull(panel.FindPairByRemotePath("/my-files/Docs"));
        Assert.NotNull(panel.FindPairByLocalPath(outer));
        Assert.Null(panel.FindPairByRemotePath("/my-files/Nope"));
        Assert.Null(panel.FindPairByLocalPath(Path.Combine(_root, "Nope")));
    }

    [Fact]
    public async Task ANestedLocalFolder_IsRefused_AndNothingIsPersisted()
    {
        var (panel, store) = Build();
        var outer = Path.Combine(_root, "Docs");
        var inner = Path.Combine(outer, "Sub");
        Directory.CreateDirectory(inner);
        var alerts = new List<string>();
        panel.RequestAlertAsync = message => { alerts.Add(message); return Task.CompletedTask; };

        Answer(panel, "/my-files/Docs", outer);
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Single(panel.Pairs);

        Answer(panel, "/my-files/Other", inner);
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(panel.Pairs);
        Assert.Single(await store.GetPairsAsync());
        Assert.Contains("overlaps", panel.StatusMessage);
        // docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2: a rejected pair looked indistinguishable
        // from a silently-failed save — the rejection must also surface as a blocking alert, not
        // just a StatusMessage line that's easy to miss.
        Assert.Contains(alerts, message => message.Contains("overlaps"));
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

    // ---------------------------------------------------------------- §12 checks that touch the disk

    [Fact]
    public async Task AnUnwritableLocalFolder_IsRefusedUpFront()
    {
        // Worth catching here rather than per file: an unwritable folder fails every single download,
        // which reads as "sync is broken" instead of "that folder is read-only".
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (panel, store) = Build();
        var readOnly = Path.Combine(_root, "read-only");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Answer(panel, "/my-files/Docs", Path.Combine(readOnly, "target"));
            await panel.AddPairCommand.ExecuteAsync();

            Assert.Empty(await store.GetPairsAsync());
            Assert.Contains("Cannot write to", panel.StatusMessage);
        }
        finally
        {
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AlreadyBusyFolder_AsksBeforeCreatingAnUploadingPair()
    {
        var (panel, store) = Build();
        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        for (var i = 0; i <= LocalFolderInspector.BusyFolderThreshold; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(busy, $"f{i}.txt"), "x");
        }

        var asked = new List<string>();
        panel.RequestConfirmationAsync = question =>
        {
            asked.Add(question);
            return Task.FromResult(false); // the user declines
        };

        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("/my-files/Docs", busy, SyncDirection.LocalToRemote, ConflictPolicy.Ask));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(asked);
        Assert.Contains("will upload all of them", asked[0]);
        Assert.Empty(await store.GetPairsAsync());
        Assert.Contains("Cancelled", panel.StatusMessage);
    }

    [Fact]
    public async Task AlreadyBusyFolder_ProceedsWhenConfirmed()
    {
        var (panel, store) = Build();
        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        for (var i = 0; i <= LocalFolderInspector.BusyFolderThreshold; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(busy, $"f{i}.txt"), "x");
        }

        panel.RequestConfirmationAsync = _ => Task.FromResult(true);
        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("/my-files/Docs", busy, SyncDirection.TwoWay, ConflictPolicy.Ask));

        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(await store.GetPairsAsync());
    }

    [Fact]
    public async Task ABusyFolder_IsNotQueriedForADownloadOnlyPair()
    {
        // A RemoteToLocal pair never uploads, so the existing contents aren't at risk of being sent
        // anywhere — and the mandatory preview shows any local deletions before they happen.
        var (panel, store) = Build();
        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        for (var i = 0; i <= LocalFolderInspector.BusyFolderThreshold; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(busy, $"f{i}.txt"), "x");
        }

        var asked = false;
        panel.RequestConfirmationAsync = _ =>
        {
            asked = true;
            return Task.FromResult(true);
        };

        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("/my-files/Docs", busy, SyncDirection.RemoteToLocal, ConflictPolicy.Ask));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.False(asked);
        Assert.Single(await store.GetPairsAsync());
    }

    [Fact]
    public async Task AnEmptyFolder_IsNeverQueried()
    {
        var (panel, store) = Build();
        panel.RequestConfirmationAsync = _ => throw new InvalidOperationException("should not have asked");

        Answer(panel, "/my-files/Docs", Path.Combine(_root, "fresh"));
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(await store.GetPairsAsync());
    }

    [Fact]
    public async Task CancellingTheDialog_ChangesNothing()
    {
        var (panel, store) = Build();
        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(null);

        await panel.AddPairCommand.ExecuteAsync();

        Assert.Empty(panel.Pairs);
        Assert.Empty(await store.GetPairsAsync());
    }
}
