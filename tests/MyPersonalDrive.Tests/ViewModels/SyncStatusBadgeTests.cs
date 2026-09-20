using System.Globalization;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// S3 of docs/PLAN-SYNC-VISIBILITY.md at the pane's edge: the statuses a listing produces, and the
/// row properties the templates bind to. The database read and the dispatcher hop around this are
/// deliberately not here — no unit test can observe either (see the note on
/// <c>MainWindowViewModel.ComputeRemoteStatuses</c>).
/// </summary>
public class SyncStatusBadgeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-badge-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static DriveItem FileItem(string name, string hash = "hash-a")
        => new($"/my-files/Docs/{name}", name, false, 100, T0, ContentHash: hash);

    [Fact]
    public async Task A_listing_inside_a_pair_gets_one_status_per_row()
    {
        var store = new SyncStateStore(_dbPath);
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false,
            new NodeFingerprint("a.txt", false, 100, T0, null, "hash-a", RemoteHashAlgorithm.Sha1),
            new NodeFingerprint("a.txt", false, 100, T0, null, "hash-a", RemoteHashAlgorithm.Sha1)), T0);
        var service = new SyncStatusService(store, "Proton Drive");
        var lookup = await service.LookupRemoteAsync("/my-files/Docs");

        var statuses = MainWindowViewModel.ComputeRemoteStatuses(lookup!, [FileItem("a.txt"), FileItem("b.txt"), FileItem("a.txt", hash: "hash-changed")]);

        Assert.Equal(SyncNodeStatus.Synced, statuses[0].Status);
        Assert.Equal(SyncNodeStatus.NotSynced, statuses[1].Status);
        Assert.Equal(SyncNodeStatus.OutOfDate, statuses[2].Status);
    }

    [Fact]
    public async Task Rows_in_a_subfolder_are_keyed_by_their_path_relative_to_the_pair()
    {
        var store = new SyncStateStore(_dbPath);
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "sub/a.txt", null, 10, 1000)], T0);
        var service = new SyncStatusService(store, "Proton Drive");
        var lookup = await service.LookupRemoteAsync("/my-files/Docs/sub");

        var statuses = MainWindowViewModel.ComputeRemoteStatuses(lookup!, [new DriveItem("/my-files/Docs/sub/a.txt", "a.txt", false, 100, T0)]);

        Assert.Equal(SyncNodeStatus.Pending, Assert.Single(statuses).Status);
    }

    [Fact]
    public void A_row_outside_every_pair_draws_no_badge()
    {
        var node = Row(new DriveItem("/my-files/x.txt", "x.txt", false));

        Assert.Equal(SyncNodeStatus.NotInPair, node.SyncStatus);
        Assert.False(node.HasSyncStatus);
    }

    [Fact]
    public void Setting_the_status_updates_the_badge_text_and_tooltip()
    {
        var node = Row(new DriveItem("/my-files/Docs/a.txt", "a.txt", false));
        var changed = new List<string>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        node.SyncStatusContext = "/my-files/Docs";
        node.SyncStatus = SyncNodeStatus.Syncing;

        Assert.True(node.HasSyncStatus);
        Assert.Equal(SyncStatusPresenter.Describe(SyncNodeStatus.Syncing), node.SyncStatusText);
        Assert.Contains("/my-files/Docs", node.SyncStatusTooltip);
        Assert.Contains(nameof(DriveNodeViewModel.HasSyncStatus), changed);
        Assert.Contains(nameof(DriveNodeViewModel.SyncStatusTooltip), changed);
    }

    [Fact]
    public void Every_status_has_its_own_wording()
    {
        var described = Enum.GetValues<SyncNodeStatus>().Select(SyncStatusPresenter.Describe).ToList();

        Assert.Equal(described.Count, described.Distinct(StringComparer.Ordinal).Count());
        Assert.All(described, text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    private static DriveNodeViewModel Row(DriveItem item)
        => new(item, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
}
