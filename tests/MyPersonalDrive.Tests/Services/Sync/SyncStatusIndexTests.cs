using System.Globalization;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// The table of (baseline, queue, listing) → status from docs/PLAN-SYNC-VISIBILITY.md §1.2,
/// including every precedence pair that matters. Pure function, no IO and no fakes: this is where
/// the bulk of the feature's test value lives, because every pane above it only forwards.
/// </summary>
public class SyncStatusIndexTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture);

    private static NodeFingerprint FileFp(string path, long size = 100, string? hash = "hash-a", DateTimeOffset? modifiedAt = null)
        => new(path, false, size, modifiedAt ?? T0, null, hash);

    private static SyncPair PairFor(bool paused = false, bool enabled = true, bool hasSynced = true, IReadOnlyList<string>? excludes = null)
        => new(
            1, "/my-files/Docs", "/home/u/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth,
            enabled, paused, excludes ?? [],
            hasSynced ? T1 : null, hasSynced ? SyncPairStatus.Ok : SyncPairStatus.Never, null);

    private static SyncStatusIndex IndexOf(
        IReadOnlyDictionary<string, SyncBaselineEntry>? baseline = null,
        IReadOnlyDictionary<string, SyncQueueState>? queue = null,
        SyncPair? pair = null)
        => new(pair ?? PairFor(), baseline ?? new Dictionary<string, SyncBaselineEntry>(), queue ?? new Dictionary<string, SyncQueueState>());

    private static Dictionary<string, SyncBaselineEntry> Baseline(params (string Path, NodeFingerprint? Fp)[] entries)
    {
        var result = new Dictionary<string, SyncBaselineEntry>(StringComparer.Ordinal);
        foreach (var (path, fp) in entries)
        {
            result[path] = new SyncBaselineEntry(path, fp is null || fp.IsFolder, fp, fp);
        }

        return result;
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public void File_outside_the_baseline_and_the_queue_is_not_synced()
        => Assert.Equal(SyncNodeStatus.NotSynced, IndexOf().StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));

    [Fact]
    public void File_matching_its_baseline_is_synced()
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt"))));
        Assert.Equal(SyncNodeStatus.Synced, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Fact]
    public void File_differing_from_its_baseline_is_out_of_date()
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt", hash: "hash-a"))));
        Assert.Equal(SyncNodeStatus.OutOfDate, index.StatusForFile("a.txt", FileFp("a.txt", hash: "hash-b"), SyncSide.Remote));
    }

    [Fact]
    public void File_whose_baseline_records_nothing_for_this_side_is_unknown()
    {
        var baseline = new Dictionary<string, SyncBaselineEntry>
        {
            ["a.txt"] = new("a.txt", false, LocalAtSync: FileFp("a.txt"), RemoteAtSync: null),
        };

        Assert.Equal(SyncNodeStatus.Unknown, IndexOf(baseline).StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Fact]
    public void File_with_no_fingerprint_in_the_listing_is_unknown_not_synced()
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt"))));
        Assert.Equal(SyncNodeStatus.Unknown, index.StatusForFile("a.txt", null, SyncSide.Remote));
    }

    [Theory]
    [InlineData(SyncQueueState.Pending, SyncNodeStatus.Pending)]
    [InlineData(SyncQueueState.Running, SyncNodeStatus.Syncing)]
    [InlineData(SyncQueueState.Failed, SyncNodeStatus.Failed)]
    [InlineData(SyncQueueState.Conflict, SyncNodeStatus.Conflict)]
    public void Queue_row_wins_over_the_baseline(SyncQueueState state, SyncNodeStatus expected)
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt"))), new Dictionary<string, SyncQueueState> { ["a.txt"] = state });
        Assert.Equal(expected, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Theory]
    [InlineData(SyncQueueState.Done)]
    [InlineData(SyncQueueState.Skipped)]
    public void Settled_queue_rows_say_nothing_and_leave_the_baseline_to_answer(SyncQueueState state)
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt"))), new Dictionary<string, SyncQueueState> { ["a.txt"] = state });
        Assert.Equal(SyncNodeStatus.Synced, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Fact]
    public void Excluded_file_says_so_rather_than_never_synced()
    {
        var index = IndexOf(pair: PairFor(excludes: ["*.log"]));
        Assert.Equal(SyncNodeStatus.Excluded, index.StatusForFile("a.log", FileFp("a.log"), SyncSide.Remote));
    }

    [Fact]
    public void Paused_pair_reports_paused_instead_of_pending_work()
    {
        var index = IndexOf(pair: PairFor(paused: true));
        Assert.Equal(SyncNodeStatus.Paused, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Fact]
    public void Paused_pair_still_reports_a_file_that_really_is_up_to_date()
    {
        var index = IndexOf(Baseline(("a.txt", FileFp("a.txt"))), pair: PairFor(paused: true));
        Assert.Equal(SyncNodeStatus.Synced, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    [Fact]
    public void Paused_pair_still_reports_a_failure_that_is_already_true()
    {
        var index = IndexOf(queue: new Dictionary<string, SyncQueueState> { ["a.txt"] = SyncQueueState.Failed }, pair: PairFor(paused: true));
        Assert.Equal(SyncNodeStatus.Failed, index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }

    // ---------------------------------------------------------------- folders

    private static SyncStatusChild File(string path, string? hash = "hash-a")
        => new(path, false, FileFp(path, hash: hash));

    [Fact]
    public void Folder_whose_listed_children_are_all_synced_is_synced()
    {
        var index = IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt")), ("Sub/b.txt", FileFp("Sub/b.txt"))));
        var status = index.StatusForFolder("Sub", [File("Sub/a.txt"), File("Sub/b.txt")], SyncSide.Remote);
        Assert.Equal(SyncNodeStatus.Synced, status);
    }

    [Fact]
    public void Folder_with_some_synced_children_is_partially_synced()
    {
        var index = IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt"))));
        var status = index.StatusForFolder("Sub", [File("Sub/a.txt"), File("Sub/b.txt")], SyncSide.Remote);
        Assert.Equal(SyncNodeStatus.PartiallySynced, status);
    }

    [Fact]
    public void Folder_with_no_synced_children_is_not_synced()
    {
        var status = IndexOf().StatusForFolder("Sub", [File("Sub/a.txt"), File("Sub/b.txt")], SyncSide.Remote);
        Assert.Equal(SyncNodeStatus.NotSynced, status);
    }

    [Fact]
    public void A_failure_below_outranks_partially_synced()
    {
        var index = IndexOf(
            Baseline(("Sub/a.txt", FileFp("Sub/a.txt"))),
            new Dictionary<string, SyncQueueState> { ["Sub/deep/b.txt"] = SyncQueueState.Failed });

        Assert.Equal(SyncNodeStatus.Failed, index.StatusForFolder("Sub", [File("Sub/a.txt")], SyncSide.Remote));
    }

    [Fact]
    public void A_running_action_deep_below_makes_a_collapsed_folder_say_syncing()
    {
        var index = IndexOf(queue: new Dictionary<string, SyncQueueState> { ["Sub/x/y/z.bin"] = SyncQueueState.Running });
        Assert.Equal(SyncNodeStatus.Syncing, index.StatusForFolder("Sub", [], SyncSide.Remote));
    }

    [Fact]
    public void A_queue_row_in_a_sibling_folder_does_not_leak_across()
    {
        var index = IndexOf(queue: new Dictionary<string, SyncQueueState> { ["Other/z.bin"] = SyncQueueState.Running });
        Assert.NotEqual(SyncNodeStatus.Syncing, index.StatusForFolder("Sub", [], SyncSide.Remote));
    }

    [Fact]
    public void A_queue_row_in_a_similarly_named_folder_does_not_leak_across()
    {
        // "Sub.bak/…" sorts between "Sub" and "Sub/" ordinally — the case a naive prefix scan
        // that stops at the first non-match would get wrong in the other direction.
        var index = IndexOf(queue: new Dictionary<string, SyncQueueState>
        {
            ["Sub.bak/z.bin"] = SyncQueueState.Running,
            ["Sub/real.bin"] = SyncQueueState.Pending,
        });

        Assert.Equal(SyncNodeStatus.Pending, index.StatusForFolder("Sub", [], SyncSide.Remote));
    }

    [Fact]
    public void Collapsed_folder_with_no_baseline_below_is_not_synced_once_a_cycle_has_run()
        => Assert.Equal(SyncNodeStatus.NotSynced, IndexOf().StatusForFolder("Sub", [], SyncSide.Remote));

    [Fact]
    public void Collapsed_folder_is_unknown_while_the_pair_has_never_run()
        => Assert.Equal(SyncNodeStatus.Unknown, IndexOf(pair: PairFor(hasSynced: false)).StatusForFolder("Sub", [], SyncSide.Remote));

    [Fact]
    public void Collapsed_baselined_folder_with_a_clean_last_cycle_is_synced()
        => Assert.Equal(SyncNodeStatus.Synced, IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt")))).StatusForFolder("Sub", [], SyncSide.Remote));

    [Fact]
    public void Collapsed_baselined_folder_after_a_partial_cycle_is_partially_synced()
    {
        var pair = PairFor() with { LastStatus = SyncPairStatus.PartialFailure };
        var index = IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt"))), pair: pair);
        Assert.Equal(SyncNodeStatus.PartiallySynced, index.StatusForFolder("Sub", [], SyncSide.Remote));
    }

    [Fact]
    public void Collapsed_baselined_folder_after_a_failed_cycle_is_unknown()
    {
        var pair = PairFor() with { LastStatus = SyncPairStatus.Error };
        var index = IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt"))), pair: pair);
        Assert.Equal(SyncNodeStatus.Unknown, index.StatusForFolder("Sub", [], SyncSide.Remote));
    }

    [Fact]
    public void Excluded_children_count_as_settled()
    {
        var index = IndexOf(Baseline(("Sub/a.txt", FileFp("Sub/a.txt"))), pair: PairFor(excludes: ["*.tmp"]));
        var status = index.StatusForFolder("Sub", [File("Sub/a.txt"), File("Sub/scratch.tmp")], SyncSide.Remote);
        Assert.Equal(SyncNodeStatus.Synced, status);
    }

    [Fact]
    public void An_expanded_grandchild_level_is_rolled_up_too()
    {
        var index = IndexOf(Baseline(("Sub/deep/a.txt", FileFp("Sub/deep/a.txt"))));
        var deep = new SyncStatusChild("Sub/deep", true, null, [File("Sub/deep/a.txt"), File("Sub/deep/b.txt")]);
        Assert.Equal(SyncNodeStatus.PartiallySynced, index.StatusForFolder("Sub", [deep], SyncSide.Remote));
    }

    [Fact]
    public void Excluded_folder_says_so_without_looking_at_its_children()
    {
        var index = IndexOf(pair: PairFor(excludes: ["node_modules/"]));
        Assert.Equal(SyncNodeStatus.Excluded, index.StatusForFolder("Sub/node_modules", [File("Sub/node_modules/a.txt")], SyncSide.Remote));
    }

    // ------------------------------------------------------------- precedence

    [Fact]
    public void Precedence_puts_attention_first_and_paused_low()
    {
        Assert.Equal(SyncNodeStatus.Failed, SyncStatusPrecedence.Max(SyncNodeStatus.Failed, SyncNodeStatus.Conflict));
        Assert.Equal(SyncNodeStatus.Conflict, SyncStatusPrecedence.Max(SyncNodeStatus.Conflict, SyncNodeStatus.Syncing));
        Assert.Equal(SyncNodeStatus.Syncing, SyncStatusPrecedence.Max(SyncNodeStatus.Syncing, SyncNodeStatus.Pending));
        Assert.Equal(SyncNodeStatus.Pending, SyncStatusPrecedence.Max(SyncNodeStatus.Pending, SyncNodeStatus.OutOfDate));
        Assert.Equal(SyncNodeStatus.OutOfDate, SyncStatusPrecedence.Max(SyncNodeStatus.OutOfDate, SyncNodeStatus.PartiallySynced));
        Assert.Equal(SyncNodeStatus.NotSynced, SyncStatusPrecedence.Max(SyncNodeStatus.NotSynced, SyncNodeStatus.Synced));
        Assert.Equal(SyncNodeStatus.Synced, SyncStatusPrecedence.Max(SyncNodeStatus.Synced, SyncNodeStatus.Paused));
        Assert.Equal(SyncNodeStatus.Paused, SyncStatusPrecedence.Max(SyncNodeStatus.Paused, SyncNodeStatus.Unknown));
    }

    [Fact]
    public void Precedence_of_an_empty_sequence_is_the_fallback()
        => Assert.Equal(SyncNodeStatus.Unknown, SyncStatusPrecedence.Max([], SyncNodeStatus.Unknown));
}
