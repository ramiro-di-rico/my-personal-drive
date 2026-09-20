using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels.Local;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels.Local;

/// <summary>
/// S4 of docs/PLAN-SYNC-VISIBILITY.md: one badge for N providers. The aggregation is the whole of
/// what the local pane adds over the cloud pane's single-pair case, so it is what is pinned here.
/// </summary>
public class LocalSyncStatusTests
{
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
}
