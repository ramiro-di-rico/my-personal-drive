using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The single ordering of <see cref="SyncNodeStatus"/> values, from "demands attention" down to
/// "nothing to say" (docs/PLAN-SYNC-VISIBILITY.md §1.1). Two callers need it and must agree:
/// a folder rolling up its descendants, and one path carrying several <c>SyncQueue</c> rows.
///
/// <see cref="SyncNodeStatus.Paused"/> sits deliberately low: a paused pair whose file is in
/// conflict should still say conflict — that the pair is paused is already stated at the pair
/// level, by the badge the row's root folder carries and by the sync panel.
/// </summary>
public static class SyncStatusPrecedence
{
    private static readonly SyncNodeStatus[] Order =
    [
        SyncNodeStatus.Failed,
        SyncNodeStatus.Conflict,
        SyncNodeStatus.Syncing,
        SyncNodeStatus.Pending,
        SyncNodeStatus.OutOfDate,
        SyncNodeStatus.PartiallySynced,
        SyncNodeStatus.NotSynced,
        SyncNodeStatus.Synced,
        SyncNodeStatus.Excluded,
        SyncNodeStatus.Paused,
        SyncNodeStatus.NotInPair,
        SyncNodeStatus.Unknown,
    ];

    /// <summary>Lower wins. Never negative: an unlisted value sorts last rather than first.</summary>
    public static int RankOf(SyncNodeStatus status)
    {
        var index = Array.IndexOf(Order, status);
        return index < 0 ? Order.Length : index;
    }

    /// <summary>The most attention-demanding of two statuses.</summary>
    public static SyncNodeStatus Max(SyncNodeStatus a, SyncNodeStatus b)
        => RankOf(a) <= RankOf(b) ? a : b;

    /// <summary>
    /// The most attention-demanding of a sequence, or <paramref name="fallback"/> when it is
    /// empty — an empty sequence is not <see cref="SyncNodeStatus.Synced"/>, it is "no evidence",
    /// and every caller here has its own honest answer for that case.
    /// </summary>
    public static SyncNodeStatus Max(IEnumerable<SyncNodeStatus> statuses, SyncNodeStatus fallback)
    {
        var best = fallback;
        var seen = false;
        foreach (var status in statuses)
        {
            best = seen ? Max(best, status) : status;
            seen = true;
        }

        return best;
    }

    /// <summary>
    /// Whether a status means "this node is settled and matches the other side". Used by the
    /// folder rollup to tell "all children agree" from "some do" — <see cref="SyncNodeStatus.Excluded"/>
    /// counts, because an excluded child is settled by definition and a folder full of
    /// <c>.tmp</c> files must not read as half-synced forever.
    /// </summary>
    public static bool IsSettled(SyncNodeStatus status)
        => status is SyncNodeStatus.Synced or SyncNodeStatus.Excluded;
}
