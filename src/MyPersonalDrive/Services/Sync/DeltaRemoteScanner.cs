using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// An <see cref="IRemoteScanner"/> for providers that can report "what changed since X"
/// (<see cref="IDeltaSource"/>) instead of requiring a full tree walk every cycle — OneDrive/Graph
/// today. <see cref="SyncReconciler"/> needs a complete remote-tree dictionary to reconcile against,
/// so this reconstructs one by merging the delta's changes onto last cycle's known-good remote
/// state, which is already sitting in the persisted three-way baseline
/// (<see cref="SyncBaselineEntry.RemoteAtSync"/>). Only ever used for a
/// <see cref="SyncDirection.TwoWay"/> pair — a one-way pair never populates a baseline, so it has
/// no merge base and always falls back to <see cref="RemoteScanner"/> instead (see
/// <c>SyncExecutor.ScanBothSidesAsync</c>). See docs/PLAN-CLOUD-PROVIDERS.md P8.
/// </summary>
public sealed class DeltaRemoteScanner : IRemoteScanner
{
    private readonly ICloudDriveProvider _provider;
    private readonly SyncStateStore _stateStore;

    public event EventHandler<NodeSkip>? NodeSkipped;

    public DeltaRemoteScanner(ICloudDriveProvider provider, SyncStateStore stateStore)
    {
        _provider = provider;
        _stateStore = stateStore;
    }

    public async Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions,
        IReadOnlyDictionary<string, SyncBaselineEntry>? baseline = null, int pairId = 0,
        CancellationToken cancellationToken = default)
    {
        var deltaSource = _provider.DeltaSource
            ?? throw new InvalidOperationException(
                $"{_provider.DisplayName} has no {nameof(IDeltaSource)}; {nameof(DeltaRemoteScanner)} requires Capabilities.SupportsDelta.");

        var merged = new Dictionary<string, NodeFingerprint>(StringComparer.Ordinal);
        if (baseline is not null)
        {
            foreach (var entry in baseline.Values)
            {
                if (entry.RemoteAtSync is not null)
                {
                    merged[entry.RelativePath] = entry.RemoteAtSync;
                }
            }
        }

        var token = await _stateStore.GetDeltaTokenAsync(pairId, cancellationToken);
        var fetched = await deltaSource.GetChangesAsync(token, cancellationToken);
        await _stateStore.SetDeltaTokenAsync(pairId, fetched.NextToken, cancellationToken);

        if (fetched.WasFullResync)
        {
            // The stored cursor had expired: every reported item is confirmed current state, not an
            // incremental diff against a gap of history this scanner can no longer reconstruct — so
            // the changes replace the baseline-derived state entirely rather than merge onto it.
            merged.Clear();
        }

        foreach (var change in fetched.Changes)
        {
            string relativePath;
            try
            {
                relativePath = pathMapper.ToRelativeFromRemote(change.Item.Path);
            }
            catch (ArgumentException)
            {
                // Whole-drive delta, filtered client-side to this pair's own subtree — everything
                // else is irrelevant to it.
                continue;
            }

            if (relativePath.Length == 0)
            {
                // The delta enumerates every item in the whole drive, including the pair's own
                // root folder as an item in its own right — something a full-walk RemoteScanner's
                // BFS can never report, since it starts *at* the root and only ever visits its
                // children. PathMapper.ToRelativeFromRemote maps that item to "", the same key
                // ToRemoteAbsolute/ToLocalAbsolute treat as "the sync root itself". Left unfiltered,
                // that key ends up in the merged dictionary as an ordinary syncable node, and the
                // reconciler — which never expects an entry for the root — can queue a real
                // TrashRemote/DeleteLocal action against relativePath "", which resolves to the
                // pair's entire root folder. Confirmed live: this is exactly how a fresh OneDrive
                // pair ended up trashing its own root folder on the very first delta cycle
                // (docs/PLAN-CLOUD-PROVIDERS.md P8's own "pending live verification" note).
                continue;
            }

            if (change.IsDeleted)
            {
                merged.Remove(relativePath);
                continue;
            }

            if (!_provider.Paths.IsRemoteNameMappableLocally(change.Item.Name))
            {
                NodeSkipped?.Invoke(this, new NodeSkip(change.Item.Name, NodeSkipReason.UnmappableName));
                merged.Remove(relativePath);
                continue;
            }

            if (exclusions.IsExcluded(relativePath, change.Item.IsFolder))
            {
                merged.Remove(relativePath);
                continue;
            }

            merged[relativePath] = new NodeFingerprint(relativePath, change.Item.IsFolder, change.Item.Size, change.Item.ModifiedAt,
                change.Item.NodeId, change.Item.ContentHash, change.Item.ContentHash is null ? null : _provider.Capabilities.RemoteHash);
        }

        var comparison = _provider.Paths.Comparison;
        return comparison == StringComparison.Ordinal ? merged : DropCaseCollisions(merged, comparison);
    }

    /// <summary>
    /// Same treatment as <see cref="RemoteScanner"/>'s own collision handling, but run once over the
    /// whole merged dictionary instead of per-BFS-sibling-batch — simpler for a flat change list,
    /// and catches collisions between items that arrived in different delta pages, which per-batch
    /// detection never could. A colliding folder's entire subtree is dropped with it: the merged
    /// dictionary already has whatever descendants existed at baseline time, so removing only the
    /// folder's own entry would silently orphan them under a name that's supposed to be excluded.
    /// </summary>
    private Dictionary<string, NodeFingerprint> DropCaseCollisions(Dictionary<string, NodeFingerprint> merged, StringComparison comparison)
    {
        var groups = merged.Values
            .GroupBy(v => ParentOf(v.RelativePath), StringComparer.Ordinal)
            .SelectMany(byParent => byParent.GroupBy(v => NameOf(v.RelativePath), StringComparer.FromComparison(comparison)))
            .ToList();

        if (groups.TrueForAll(group => group.Count() == 1))
        {
            return merged;
        }

        var toRemove = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Count() == 1)
            {
                continue;
            }

            foreach (var collided in group)
            {
                NodeSkipped?.Invoke(this, new NodeSkip(NameOf(collided.RelativePath), NodeSkipReason.CaseCollision));
                toRemove.Add(collided.RelativePath);

                if (collided.IsFolder)
                {
                    var prefix = collided.RelativePath + "/";
                    foreach (var key in merged.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        toRemove.Add(key);
                    }
                }
            }
        }

        foreach (var key in toRemove)
        {
            merged.Remove(key);
        }

        return merged;
    }

    private static string ParentOf(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : relativePath[..lastSlash];
    }

    private static string NameOf(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? relativePath : relativePath[(lastSlash + 1)..];
    }
}
