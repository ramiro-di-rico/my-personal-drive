using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests;
using MyPersonalDrive.ViewModels.Local;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels.Local;

/// <summary>
/// S4 of docs/PLAN-SYNC-VISIBILITY.md: one badge for N providers. The aggregation is the whole of
/// what the local pane adds over the cloud pane's single-pair case, so it is what is pinned here.
/// </summary>
[Collection(AppDataCollection.Name)]
public class LocalSyncStatusTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalStatus.AppData").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalStatus.Root").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-localstatus-{Guid.NewGuid():N}.db");
    private readonly string? _originalAppData;

    public LocalSyncStatusTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            Directory.Delete(_root, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static (SyncNodeStatus Status, string? Context) Aggregate(params (string Account, SyncNodeStatus Status)[] statuses)
        => LocalExplorerViewModel.Aggregate(statuses);

    [Fact]
    public void No_pair_means_no_badge()
    {
        var (status, context) = Aggregate();

        Assert.Equal(SyncNodeStatus.NotInPair, status);
        Assert.Null(context);
    }

    [Fact]
    public void One_pair_is_reported_verbatim_and_named()
    {
        var (status, context) = Aggregate(("OneDrive", SyncNodeStatus.OutOfDate));

        Assert.Equal(SyncNodeStatus.OutOfDate, status);
        Assert.Equal("OneDrive", context);
    }

    [Fact]
    public void Every_provider_up_to_date_is_synced_and_names_them_all()
    {
        var (status, context) = Aggregate(("Proton Drive", SyncNodeStatus.Synced), ("OneDrive", SyncNodeStatus.Synced));

        Assert.Equal(SyncNodeStatus.Synced, status);
        Assert.Contains("Proton Drive", context);
        Assert.Contains("OneDrive", context);
    }

    [Fact]
    public void Some_up_to_date_and_some_not_is_partial_with_the_counts()
    {
        var (status, context) = Aggregate(
            ("Proton Drive", SyncNodeStatus.Synced),
            ("OneDrive", SyncNodeStatus.NotSynced),
            ("Google Drive", SyncNodeStatus.Synced));

        Assert.Equal(SyncNodeStatus.PartiallySynced, status);
        Assert.Contains("2", context);
        Assert.Contains("3", context);
    }

    [Fact]
    public void A_failure_on_one_provider_outranks_the_others_being_fine()
    {
        var (status, _) = Aggregate(("Proton Drive", SyncNodeStatus.Synced), ("OneDrive", SyncNodeStatus.Failed));

        Assert.Equal(SyncNodeStatus.Failed, status);
    }

    [Fact]
    public void A_conflict_on_one_provider_outranks_a_pending_transfer_on_another()
    {
        var (status, _) = Aggregate(("Proton Drive", SyncNodeStatus.Pending), ("OneDrive", SyncNodeStatus.Conflict));

        Assert.Equal(SyncNodeStatus.Conflict, status);
    }

    [Fact]
    public void No_provider_up_to_date_reports_the_worst_of_them_not_partial()
    {
        var (status, _) = Aggregate(("Proton Drive", SyncNodeStatus.NotSynced), ("OneDrive", SyncNodeStatus.Syncing));

        Assert.Equal(SyncNodeStatus.Syncing, status);
    }

    [Fact]
    public void A_local_row_starts_with_no_badge()
    {
        var row = new LocalNodeViewModel(new DriveItem("/home/u/a.txt", "a.txt", false), _ => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Equal(SyncNodeStatus.NotInPair, row.SyncStatus);
        Assert.False(row.HasSyncStatus);
    }

    /// <summary>
    /// S6: a finished sync cycle has to reach the rows already on screen, and it must do it without
    /// re-listing the folder — the point of the invalidate-and-re-ask path.
    /// </summary>
    [Fact]
    public async Task A_sync_event_updates_the_rows_already_on_screen_without_re_listing()
    {
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "hello");

        var store = new SyncStateStore(_dbPath);
        var pair = await store.CreatePairAsync("/my-files/Docs", _root, SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        var service = new SyncStatusService(store, "Proton Drive");

        var listings = 0;
        var sut = new LocalExplorerViewModel(new CountingLocalFileSystemService(_root, () => listings++), new AppSettingsService())
        {
            LookupSyncStatusesAsync = path => service.LookupLocalAsync(path),
        };

        await sut.NavigateAsync(_root);

        // The annotation the listing kicks off is fire-and-forget, so this awaits the same work
        // rather than racing it.
        await sut.RefreshSyncStatusesAsync();
        var row = sut.Items.Single(item => item.Item.Name == "a.txt");
        Assert.Equal(SyncNodeStatus.NotSynced, row.SyncStatus);

        // What a completed cycle leaves behind, then the invalidation the scheduler's event causes.
        await store.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.UploadFile, "a.txt", null, 5, 1000)], DateTimeOffset.UnixEpoch);
        var listingsBefore = listings;
        service.Invalidate();
        await sut.RefreshSyncStatusesAsync();

        Assert.Equal(SyncNodeStatus.Pending, row.SyncStatus);
        Assert.Equal(listingsBefore, listings);
        Assert.Same(row, sut.Items.Single(item => item.Item.Name == "a.txt"));
    }

    private sealed class CountingLocalFileSystemService(string home, Action onList) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;

        public override IReadOnlyList<DriveItem> ListDirectory(string path, bool includeHidden)
        {
            onList();
            return base.ListDirectory(path, includeHidden);
        }
    }
}
