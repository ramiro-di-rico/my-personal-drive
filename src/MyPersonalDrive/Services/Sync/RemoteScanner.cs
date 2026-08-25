using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services.Sync;

public interface IRemoteScanner
{
    /// <summary>
    /// Raised for a node the scanner deliberately left out because its name can't be represented
    /// locally. Reported rather than silently dropped: a file the user can see in Proton Drive but
    /// never in their synced folder needs an explanation, not silence.
    /// </summary>
    event EventHandler<string>? NodeSkipped;

    Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions, CancellationToken cancellationToken = default);
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

    public event EventHandler<string>? NodeSkipped;

    public RemoteScanner(ICloudDriveProvider provider, int maxConcurrency = 0)
    {
        _provider = provider;
        _walker = new RemoteTreeWalker(provider, maxConcurrency);
    }

    public async Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions, CancellationToken cancellationToken = default)
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
        // Only populated (and only checked) for a case-insensitive provider — see
        // ReportCaseCollisionIfAny. Empty for Proton, so this costs nothing today.
        var seenByFold = _provider.Paths.Comparison == StringComparison.Ordinal
            ? null
            : new Dictionary<string, string>(StringComparer.FromComparison(_provider.Paths.Comparison));

        await _walker.WalkAsync(remoteRoot, item =>
        {
            // A name containing '/' cannot exist as a local filename on Linux, so this node
            // can't be mirrored under its real name. Skipping keeps the engine's whole
            // identity model intact — relative paths stay unambiguously '/'-separated — where
            // the alternative would be to invent a substitute name, which in a TwoWay pair
            // would then upload back as a second, differently-named copy.
            if (!_provider.Paths.IsRemoteNameMappableLocally(item.Name))
            {
                NodeSkipped?.Invoke(this, item.Name);
                return false;
            }

            var relativePath = pathMapper.ToRelativeFromRemote(item.Path);
            if (exclusions.IsExcluded(relativePath, item.IsFolder))
            {
                return false;
            }

            if (seenByFold is not null && !ReportCaseCollisionIfAny(seenByFold, relativePath, result))
            {
                return false;
            }

            result[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash,
                item.ContentHash is null ? null : _provider.Capabilities.RemoteHash);
            return true;
        }, cancellationToken: cancellationToken);

        return result;
    }

    /// <summary>
    /// Two remote paths that differ only by case are one local path on a case-insensitive
    /// provider — e.g. OneDrive's <c>Photos/</c> and <c>photos/</c> would both map to one local
    /// folder (docs/PLAN-CLOUD-PROVIDERS.md §2.4). Rather than let the second one silently
    /// overwrite the first in <c>result</c>, or invent which one "wins", both are skipped and
    /// reported — same treatment as an unmappable name. Never happens on Proton: <paramref
    /// name="seenByFold"/> is only non-empty for a provider whose <c>Paths.Comparison</c> ignores
    /// case.
    /// </summary>
    private bool ReportCaseCollisionIfAny(Dictionary<string, string> seenByFold, string relativePath, Dictionary<string, NodeFingerprint> result)
    {
        if (seenByFold.TryGetValue(relativePath, out var existing))
        {
            NodeSkipped?.Invoke(this, relativePath);
            if (!string.Equals(existing, relativePath, StringComparison.Ordinal))
            {
                // The first occurrence was already added to `result` under its own casing before
                // the second arrived; it must be retracted too, since neither can be trusted alone.
                NodeSkipped?.Invoke(this, existing);
                result.Remove(existing);
            }

            return false;
        }

        seenByFold[relativePath] = relativePath;
        return true;
    }
}
