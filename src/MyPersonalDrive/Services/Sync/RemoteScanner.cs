using MyPersonalDrive.Models;

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
    private readonly ProtonDriveService _service;
    private readonly RemoteTreeWalker _walker;

    public event EventHandler<string>? NodeSkipped;

    public RemoteScanner(ProtonDriveService service, int maxConcurrency = 0)
    {
        _service = service;
        _walker = new RemoteTreeWalker(service, maxConcurrency);
    }

    public async Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions, CancellationToken cancellationToken = default)
    {
        // Once per scan, never per folder. `filesystem list` answers from the CLI's cache and never
        // revalidates a folder it has already listed (Appendix A #16), so a scan starting from a
        // warm cache can silently omit nodes that exist — which a TwoWay pair would then read as
        // remote deletions. Within the scan the cache is left alone: it is what keeps the walk from
        // paying a cold start per folder, and a few seconds of drift inside one scan is the same
        // staleness window the engine already tolerates.
        await _service.ResetRemoteCacheAsync(cancellationToken);

        var result = new Dictionary<string, NodeFingerprint>();

        await _walker.WalkAsync(remoteRoot, item =>
        {
            // A name containing '/' cannot exist as a local filename on Linux, so this node
            // can't be mirrored under its real name. Skipping keeps the engine's whole
            // identity model intact — relative paths stay unambiguously '/'-separated — where
            // the alternative would be to invent a substitute name, which in a TwoWay pair
            // would then upload back as a second, differently-named copy.
            if (ProtonDriveService.HasUnmappableName(item.Name))
            {
                NodeSkipped?.Invoke(this, item.Name);
                return false;
            }

            var relativePath = pathMapper.ToRelativeFromRemote(item.Path);
            if (exclusions.IsExcluded(relativePath, item.IsFolder))
            {
                return false;
            }

            result[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash);
            return true;
        }, cancellationToken: cancellationToken);

        return result;
    }
}
