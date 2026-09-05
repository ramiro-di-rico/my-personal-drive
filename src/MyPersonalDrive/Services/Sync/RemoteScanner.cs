using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services.Sync;

/// <summary>Why <see cref="IRemoteScanner.NodeSkipped"/> fired for a given node.</summary>
public enum NodeSkipReason
{
    /// <summary>The name can't exist as a local filename at all (e.g. contains '/' on Linux).</summary>
    UnmappableName,

    /// <summary>The name collides with a sibling once the provider's comparison ignores case.</summary>
    CaseCollision,

    /// <summary>
    /// The name is an exact duplicate of a sibling's on a provider that allows two same-named
    /// siblings distinguished only by an internal id (Google Drive —
    /// <see cref="Providers.IProviderPathSyntax.AllowsDuplicateNamesInSameParent"/>,
    /// docs/PLAN-CLOUD-PROVIDERS.md §8.2/G2). Distinct from <see cref="CaseCollision"/> only in
    /// wording: the underlying "every member of the colliding group is dropped, first wins nothing"
    /// handling is identical.
    /// </summary>
    DuplicateName,

    /// <summary>
    /// A remote-only node with no binary content to sync against — a Google-native file (Docs,
    /// Sheets, Slides, Forms, Drawings), which has no checksum at all
    /// (docs/PLAN-CLOUD-PROVIDERS.md §8.4/G4). See <see cref="Models.DriveItem.IsRemoteOnlyDocument"/>.
    /// </summary>
    GoogleNativeFile
}

/// <summary>The node <see cref="IRemoteScanner.NodeSkipped"/> reports, and why.</summary>
public readonly record struct NodeSkip(string Name, NodeSkipReason Reason);

public interface IRemoteScanner
{
    /// <summary>
    /// Raised for a node the scanner deliberately left out because it can't be represented
    /// locally under its real name. Reported rather than silently dropped: a file the user can
    /// see in Proton Drive but never in their synced folder needs an explanation, not silence.
    /// </summary>
    event EventHandler<NodeSkip>? NodeSkipped;

    /// <param name="baseline">
    /// Last cycle's known-good three-way baseline, keyed by relative path — the merge base a
    /// delta-based scanner needs to reconstruct a complete remote-tree dictionary from a partial
    /// "what changed" page (see <see cref="DeltaRemoteScanner"/>). A full-walk scanner like
    /// <see cref="RemoteScanner"/> ignores it: it always produces a complete dictionary on its own.
    /// Null for a one-way pair, which never populates a baseline in the first place
    /// (docs/PLAN-CLOUD-PROVIDERS.md P8).
    /// </param>
    /// <param name="pairId">
    /// The sync pair's id — needed only by a scanner with its own per-pair persisted state (a
    /// delta cursor); ignored by <see cref="RemoteScanner"/>.
    /// </param>
    Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions,
        IReadOnlyDictionary<string, SyncBaselineEntry>? baseline = null, int pairId = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fingerprints a remote subtree for the sync reconciler: every node under <c>remoteRoot</c>, keyed
/// by its sync-relative path, minus the ones the pair excludes or cannot represent locally.
///
/// The walk itself — BFS wave by wave, its concurrency, and why that is safe on this CLI — lives in
/// <see cref="RemoteTreeWalker"/>, shared with the folder-metrics scanner
/// (docs/PLAN-BROWSER-VIEWS.md M3). What stays here is the part that is specific to sync: the
/// identity model, the exclusions, and the cache reset below.
/// </summary>
public sealed class RemoteScanner : IRemoteScanner
{
    private readonly ICloudDriveProvider _provider;
    private readonly RemoteTreeWalker _walker;

    public event EventHandler<NodeSkip>? NodeSkipped;

    public RemoteScanner(ICloudDriveProvider provider, int maxConcurrency = 0)
    {
        _provider = provider;
        _walker = new RemoteTreeWalker(provider, maxConcurrency);
    }

    public async Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions,
        IReadOnlyDictionary<string, SyncBaselineEntry>? baseline = null, int pairId = 0,
        CancellationToken cancellationToken = default)
    {
        // Once per scan, never per folder. `filesystem list` answers from the CLI's cache and never
        // revalidates a folder it has already listed (Appendix A #16), so a scan starting from a
        // warm cache can silently omit nodes that exist — which a TwoWay pair would then read as
        // remote deletions. Within the scan the cache is left alone: it is what keeps the walk from
        // paying a cold start per folder, and a few seconds of drift inside one scan is the same
        // staleness window the engine already tolerates. Null on a provider with nothing to
        // invalidate (docs/PLAN-CLOUD-PROVIDERS.md §2.4).
        if (_provider.RemoteView is not null)
        {
            await _provider.RemoteView.ResetRemoteCacheAsync(cancellationToken);
        }

        var result = new Dictionary<string, NodeFingerprint>();
        var comparison = _provider.Paths.Comparison;

        await _walker.WalkAsync(remoteRoot, item =>
        {
            // A name containing '/' cannot exist as a local filename on Linux, so this node
            // can't be mirrored under its real name. Skipping keeps the engine's whole
            // identity model intact — relative paths stay unambiguously '/'-separated — where
            // the alternative would be to invent a substitute name, which in a TwoWay pair
            // would then upload back as a second, differently-named copy.
            if (!_provider.Paths.IsRemoteNameMappableLocally(item.Name))
            {
                NodeSkipped?.Invoke(this, new NodeSkip(item.Name, NodeSkipReason.UnmappableName));
                return false;
            }

            // A Google-native file (Docs/Sheets/Slides/...) has no binary content and therefore no
            // checksum to sync against at all — treated as a skip, same as an unmappable name,
            // rather than attempting an export-to-binary conversion (docs/PLAN-CLOUD-PROVIDERS.md
            // §8.4/G4, explicitly deferred past P10).
            if (item.IsRemoteOnlyDocument)
            {
                NodeSkipped?.Invoke(this, new NodeSkip(item.Name, NodeSkipReason.GoogleNativeFile));
                return false;
            }

            var relativePath = pathMapper.ToRelativeFromRemote(item.Path);
            if (exclusions.IsExcluded(relativePath, item.IsFolder))
            {
                return false;
            }

            result[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash,
                item.ContentHash is null ? null : _provider.Capabilities.RemoteHash);
            return true;
        },
        // Only exercised for a case-insensitive provider, or one that allows exact duplicate
        // names in the same parent (Google Drive) — a no-op passthrough for Proton (Comparison ==
        // Ordinal and no duplicate names allowed), so this costs nothing today.
        filterSiblings: comparison == StringComparison.Ordinal && !_provider.Paths.AllowsDuplicateNamesInSameParent
            ? null
            : siblings => DropNameCollisions(siblings, comparison, _provider.Paths.AllowsDuplicateNamesInSameParent),
        cancellationToken: cancellationToken);

        return result;
    }

    /// <summary>
    /// Two remote names that differ only by case are one local name on a case-insensitive
    /// provider — e.g. OneDrive's <c>Photos/</c> and <c>photos/</c> would both map to one local
    /// folder (docs/PLAN-CLOUD-PROVIDERS.md §2.4). Rather than let one silently overwrite the
    /// other, or invent which one "wins", every name in a colliding group is dropped and
    /// reported — same treatment as an unmappable name.
    ///
    /// Runs once per <see cref="RemoteTreeWalker.WalkAsync"/> listing (i.e. once per set of true
    /// siblings — same parent), <em>before</em> any of them reach the per-item callback above.
    /// That ordering is what makes this correct: a colliding folder is dropped here, so it is
    /// never queued for the walk's next wave in the first place. Deciding per-item instead (drop
    /// the second occurrence, retract the first from <c>result</c>) was tried and found unsound —
    /// by the time a later sibling reveals the collision, an earlier folder sibling may already
    /// be queued to descend into, and retracting it from <c>result</c> doesn't stop that descent:
    /// its children still get walked and added, leaking part of a folder that was supposed to be
    /// entirely excluded.
    ///
    /// Extended for Google Drive (docs/PLAN-CLOUD-PROVIDERS.md §8.2/G2): a provider whose
    /// <see cref="IProviderPathSyntax.AllowsDuplicateNamesInSameParent"/> is true can hold two
    /// exact-same-named siblings distinguished only by an internal id — grouping by
    /// <see cref="StringComparison.Ordinal"/> already detects that exact-duplicate case correctly;
    /// only the reported <see cref="NodeSkipReason"/> differs from a case-insensitive collision.
    /// </summary>
    private IReadOnlyList<DriveItem> DropNameCollisions(IReadOnlyList<DriveItem> siblings, StringComparison comparison, bool isDuplicateNameProvider)
    {
        if (siblings.Count < 2)
        {
            return siblings;
        }

        var byFold = siblings.GroupBy(s => s.Name, StringComparer.FromComparison(comparison)).ToList();
        if (byFold.TrueForAll(group => group.Count() == 1))
        {
            return siblings;
        }

        var reason = isDuplicateNameProvider ? NodeSkipReason.DuplicateName : NodeSkipReason.CaseCollision;
        var survivors = new List<DriveItem>(siblings.Count);
        foreach (var group in byFold)
        {
            if (group.Count() == 1)
            {
                survivors.Add(group.Single());
                continue;
            }

            foreach (var collided in group)
            {
                NodeSkipped?.Invoke(this, new NodeSkip(collided.Name, reason));
            }
        }

        return survivors;
    }
}
