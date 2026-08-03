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
/// <b>Concurrency defaults to 1 on purpose.</b> It used to default to 3, on the strength of
/// Appendix A #11's single trial of four parallel `list` calls. Re-testing found that trial was
/// simply lucky: concurrent `proton-drive` processes intermittently crash on the CLI's *own*
/// internal SQLite cache with `SQLITE_BUSY` (~1 in 3 calls in a three-way race), taking the whole
/// scan down with them. Raise this only against a CLI version verified to serialize its cache
/// access.
/// </summary>
public sealed class RemoteScanner : IRemoteScanner
{
    private readonly ProtonDriveService _service;
    private readonly int _maxConcurrency;

    public event EventHandler<string>? NodeSkipped;

    public RemoteScanner(ProtonDriveService service, int maxConcurrency = 1)
    {
        _service = service;
        _maxConcurrency = maxConcurrency;
    }

    public async Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions, CancellationToken cancellationToken = default)
    {
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
