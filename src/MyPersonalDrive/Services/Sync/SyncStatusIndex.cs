using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Answers "what is this file's / this folder's sync status?" for one sync pair, from a snapshot
/// of that pair's <c>SyncState</c> baseline and <c>SyncQueue</c> rows
/// (docs/PLAN-SYNC-VISIBILITY.md §1.2).
///
/// Pure and immutable: constructed from the snapshot, never touches the database, the disk or the
/// provider, and answers every query from memory. That is the constraint the whole feature rests
/// on — a badge must cost nothing to draw, or browsing a folder would fire one query per row.
/// Refreshing it is <see cref="SyncStatusService"/>'s job, by building a new one.
///
/// It deliberately has no notion of "the current state of the world" beyond what the caller hands
/// it per query: the pane already holds the listing it is drawing, with each node's live
/// size/mtime/hash, so asking the index to fetch that again would be the one expensive thing this
/// design exists to avoid.
/// </summary>
public sealed class SyncStatusIndex
{
    private readonly SyncPair _pair;
    private readonly IReadOnlyDictionary<string, SyncBaselineEntry> _baseline;
    private readonly IReadOnlyDictionary<string, SyncQueueState> _queue;
    private readonly ExclusionMatcher _exclusions;
    private readonly TimeSpan _tolerance;

    /// <summary>Baseline and queue keys, sorted, for the prefix scans the folder rollup needs.</summary>
    private readonly string[] _baselinePaths;
    private readonly string[] _queuePaths;

    public SyncStatusIndex(
        SyncPair pair,
        IReadOnlyDictionary<string, SyncBaselineEntry> baseline,
        IReadOnlyDictionary<string, SyncQueueState> queueStates,
        ExclusionMatcher? exclusions = null,
        TimeSpan? mtimeTolerance = null)
    {
        _pair = pair;
        _baseline = baseline;
        _queue = queueStates;
        _exclusions = exclusions ?? new ExclusionMatcher(pair.ExcludeGlobs);
        _tolerance = mtimeTolerance ?? NodeFingerprintComparer.DefaultMtimeTolerance;

        _baselinePaths = baseline.Keys.ToArray();
        Array.Sort(_baselinePaths, StringComparer.Ordinal);
        _queuePaths = queueStates.Keys.ToArray();
        Array.Sort(_queuePaths, StringComparer.Ordinal);
    }

    /// <summary>The pair this index describes — callers label their rows with it.</summary>
    public SyncPair Pair => _pair;

    /// <summary>
    /// True when the pair has completed at least one cycle. The rollup needs it: only then does
    /// "no baseline row anywhere under this folder" mean <see cref="SyncNodeStatus.NotSynced"/>
    /// as a fact rather than as a guess about a pair that has simply never run.
    /// </summary>
    private bool HasSyncedOnce => _pair.LastSyncAt is not null && _pair.LastStatus != SyncPairStatus.Never;

    /// <summary>
    /// One file's status. <paramref name="current"/> is the fingerprint from the listing the pane
    /// is already showing; null means the caller has no fingerprint to offer (a provider that
    /// reports neither size nor mtime), which downgrades a baselined file to
    /// <see cref="SyncNodeStatus.Unknown"/> rather than claiming it is up to date.
    /// </summary>
    public SyncNodeStatus StatusForFile(string relativePath, NodeFingerprint? current, SyncSide side)
    {
        if (QueueStatus(relativePath) is { } queued)
        {
            return queued;
        }

        if (_exclusions.IsExcluded(relativePath, isDirectory: false))
        {
            return SyncNodeStatus.Excluded;
        }

        if (!_baseline.TryGetValue(relativePath, out var entry))
        {
            return WithPause(SyncNodeStatus.NotSynced);
        }

        var atSync = side == SyncSide.Local ? entry.LocalAtSync : entry.RemoteAtSync;
        if (atSync is null || current is null)
        {
            // A baseline row that records nothing for this side (a one-way pair keeps only the
            // side it wrote), or a listing that carries no fingerprint at all. Either way there
            // is nothing to compare, and "synced" would be an assertion we cannot make.
            return SyncNodeStatus.Unknown;
        }

        return NodeFingerprintComparer.AreEquivalent(current, atSync, _tolerance)
            ? WithPause(SyncNodeStatus.Synced)
            : WithPause(SyncNodeStatus.OutOfDate);
    }

    /// <summary>
    /// One folder's status: the precedence-max of every queue row under its prefix and the status
    /// of the children the caller actually listed, plus
    /// <see cref="SyncNodeStatus.PartiallySynced"/> when those children disagree.
    ///
    /// <paramref name="listedChildren"/> is what the pane is showing for this folder — empty for a
    /// collapsed folder, which is exactly the case §1.2 refuses to guess about.
    /// </summary>
    public SyncNodeStatus StatusForFolder(
        string relativePath,
        IReadOnlyList<SyncStatusChild> listedChildren,
        SyncSide side)
    {
        if (_exclusions.IsExcluded(relativePath, isDirectory: true))
        {
            return SyncNodeStatus.Excluded;
        }

        // A queue row anywhere below is the strongest signal there is: it is what makes a
        // collapsed folder read "sincronizando" while a file six levels down uploads.
        var fromQueue = SyncStatusPrecedence.Max(QueueStatusesUnder(relativePath), SyncNodeStatus.Unknown);
        if (fromQueue is not SyncNodeStatus.Unknown)
        {
            return fromQueue;
        }

        if (listedChildren.Count > 0)
        {
            var childStatuses = listedChildren
                .Select(child => child.IsFolder
                    ? StatusForFolder(child.RelativePath, child.Children ?? [], side)
                    : StatusForFile(child.RelativePath, child.Fingerprint, side))
                .ToList();

            var settled = childStatuses.Count(SyncStatusPrecedence.IsSettled);
            if (settled == childStatuses.Count)
            {
                return WithPause(SyncNodeStatus.Synced);
            }

            var worst = SyncStatusPrecedence.Max(childStatuses, SyncNodeStatus.Unknown);

            // Some children are fine and some are not: that is precisely "partially synced", and
            // it outranks whatever the unsettled ones individually say only when they are merely
            // "not synced yet" — an actual failure or conflict below is the more useful answer.
            if (settled > 0 && SyncStatusPrecedence.RankOf(worst) >= SyncStatusPrecedence.RankOf(SyncNodeStatus.PartiallySynced))
            {
                return WithPause(SyncNodeStatus.PartiallySynced);
            }

            return WithPause(worst);
        }

        // Nothing listed. The one confident answer left: the pair has run a full cycle and left no
        // baseline row under this folder at all, so it really has never been synced.
        if (!HasAnyBaselineUnder(relativePath))
        {
            return HasSyncedOnce ? WithPause(SyncNodeStatus.NotSynced) : SyncNodeStatus.Unknown;
        }

        if (!HasSyncedOnce)
        {
            return SyncNodeStatus.Unknown;
        }

        // Baselined, nothing queued, and a cycle has completed: as of that cycle everything under
        // here was equal on both sides, and anything that had not been would have left a queue row
        // (the branch above) — so this is a statement about the last cycle, not a guess, and the
        // tooltip says so. A cycle that only partly succeeded cannot make that claim.
        // Refines docs/PLAN-SYNC-VISIBILITY.md §1.2, which left this case Unknown: in practice
        // every collapsed folder in a healthy pair fell into it, so the pane was a field of
        // question marks that told the user nothing.
        return _pair.LastStatus switch
        {
            SyncPairStatus.Ok => WithPause(SyncNodeStatus.Synced),
            SyncPairStatus.PartialFailure => WithPause(SyncNodeStatus.PartiallySynced),
            _ => SyncNodeStatus.Unknown,
        };
    }

    /// <summary>
    /// A paused or disabled pair reports <see cref="SyncNodeStatus.Paused"/> in place of the
    /// states that describe pending work — the honest answer for "when will this be fixed?" is
    /// "not until you resume". States that outrank <see cref="SyncNodeStatus.Paused"/> and are
    /// already true right now (failed, conflict) pass through untouched, per §1.1, and never
    /// reach here: the queue branch returns before this is called.
    /// </summary>
    private SyncNodeStatus WithPause(SyncNodeStatus status)
    {
        if (_pair.IsEnabled && !_pair.IsPaused)
        {
            return status;
        }

        return status is SyncNodeStatus.Synced ? status : SyncNodeStatus.Paused;
    }

    private SyncNodeStatus? QueueStatus(string relativePath)
        => _queue.TryGetValue(relativePath, out var state) ? FromQueueState(state) : null;

    private IEnumerable<SyncNodeStatus> QueueStatusesUnder(string relativePath)
    {
        foreach (var path in PathsUnder(_queuePaths, relativePath))
        {
            if (FromQueueState(_queue[path]) is { } status)
            {
                yield return status;
            }
        }
    }

    private bool HasAnyBaselineUnder(string relativePath)
        => PathsUnder(_baselinePaths, relativePath).Any();

    /// <summary>
    /// Every key that is <paramref name="relativePath"/> itself or a descendant of it. Binary
    /// search into the sorted key array rather than a scan of all of them: a pane refresh asks
    /// this once per visible folder, against a baseline that can hold six figures of rows.
    /// </summary>
    private static IEnumerable<string> PathsUnder(string[] sorted, string relativePath)
    {
        var prefix = relativePath.Length == 0 ? string.Empty : relativePath + "/";
        var start = Array.BinarySearch(sorted, relativePath.Length == 0 ? string.Empty : relativePath, StringComparer.Ordinal);
        if (start < 0)
        {
            start = ~start;
        }

        for (var i = start; i < sorted.Length; i++)
        {
            var candidate = sorted[i];
            if (candidate == relativePath)
            {
                yield return candidate;
                continue;
            }

            if (prefix.Length > 0 && !candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Sorted ordinally, so the first key that does not share the prefix ends the run —
                // but only once we are past the folder's own key, handled above.
                if (string.CompareOrdinal(candidate, prefix) > 0)
                {
                    yield break;
                }

                continue;
            }

            yield return candidate;
        }
    }

    /// <summary>
    /// A queue row's state as a node status. <c>Done</c> and <c>Skipped</c> return null — a
    /// completed row says nothing about the node's current state, which the baseline answers
    /// better, and letting "done" win would freeze a row at "synced" until the row is pruned.
    /// </summary>
    private static SyncNodeStatus? FromQueueState(SyncQueueState state) => state switch
    {
        SyncQueueState.Pending => SyncNodeStatus.Pending,
        SyncQueueState.Running => SyncNodeStatus.Syncing,
        SyncQueueState.Failed => SyncNodeStatus.Failed,
        SyncQueueState.Conflict => SyncNodeStatus.Conflict,
        _ => null,
    };
}

/// <summary>
/// One child of a folder being rolled up: the listing row the pane already holds.
/// <see cref="Children"/> is for a tree view that has a grandchild level expanded; null or empty
/// means "not listed", which is what makes the parent answer <see cref="SyncNodeStatus.Unknown"/>
/// instead of guessing.
/// </summary>
public sealed record SyncStatusChild(
    string RelativePath,
    bool IsFolder,
    NodeFingerprint? Fingerprint,
    IReadOnlyList<SyncStatusChild>? Children = null);
