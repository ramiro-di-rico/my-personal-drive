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
/// BFS over the remote tree via repeated `filesystem list` calls — the CLI has no recursive
/// listing (docs/PLAN-LOCAL-SYNC.md Appendix A #4). Processes one "wave" (depth level) of
/// folders at a time, bounded by <paramref name="maxConcurrency"/>, which also happens to
/// match the plan's folder-creation-shallowest-first ordering for free. Each CLI call costs
/// ~3.5s of process-startup overhead regardless of folder size (Appendix A #11a), and subtree
/// caching is impossible with this CLI (#11b), so a scan is unavoidably O(folders) × 3.5s.
///
/// <b>Concurrency is back above 1</b>, but for a different reason than the original Appendix A #11
/// trial claimed. That trial got lucky; concurrent `proton-drive` processes really do crash on the
/// CLI's own SQLite cache with `SQLITE_BUSY`, and still do in cli-drive@0.6.0. What Appendix A #16
/// established is that the contention is entirely over the one *shared* cache file, so
/// <see cref="ProtonDriveCliExecutor"/> now gives each concurrent process a private
/// `XDG_CACHE_HOME` — measured at 64 clean calls out of 64 with eight in flight, against 15
/// failures in 64 sharing one cache. The executor is what makes this safe; this number only says
/// how wide the BFS wave is allowed to get.
///
/// Default of 0 defers to the executor's own ceiling, which is derived from the CPU count — the
/// ~3.5s per call is Node.js startup, so cores are the real limit.
/// </summary>
public sealed class RemoteScanner : IRemoteScanner
{
    private readonly ProtonDriveService _service;
    private readonly int _maxConcurrency;

    public event EventHandler<string>? NodeSkipped;

    public RemoteScanner(ProtonDriveService service, int maxConcurrency = 0)
    {
        _service = service;
        _maxConcurrency = maxConcurrency > 0
            ? maxConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 8);
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
        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        var currentWave = new List<string> { remoteRoot };

        while (currentWave.Count > 0)
        {
            var listings = await Task.WhenAll(
                currentWave.Select(folderPath => ListOneFolderAsync(folderPath, semaphore, cancellationToken)));

            var nextWave = new List<string>();
            foreach (var items in listings)
            {
                foreach (var item in items)
                {
                    // A name containing '/' cannot exist as a local filename on Linux, so this node
                    // can't be mirrored under its real name. Skipping keeps the engine's whole
                    // identity model intact — relative paths stay unambiguously '/'-separated — where
                    // the alternative would be to invent a substitute name, which in a TwoWay pair
                    // would then upload back as a second, differently-named copy.
                    if (ProtonDriveService.HasUnmappableName(item.Name))
                    {
                        NodeSkipped?.Invoke(this, item.Name);
                        continue;
                    }

                    var relativePath = pathMapper.ToRelativeFromRemote(item.Path);
                    if (exclusions.IsExcluded(relativePath, item.IsFolder))
                    {
                        continue;
                    }

                    result[relativePath] = new NodeFingerprint(relativePath, item.IsFolder, item.Size, item.ModifiedAt, item.NodeId, item.ContentHash);
                    if (item.IsFolder)
                    {
                        nextWave.Add(item.Path);
                    }
                }
            }

            currentWave = nextWave;
        }

        return result;
    }

    private async Task<IReadOnlyList<DriveItem>> ListOneFolderAsync(string folderPath, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await _service.LoadFolderAsync(folderPath, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
