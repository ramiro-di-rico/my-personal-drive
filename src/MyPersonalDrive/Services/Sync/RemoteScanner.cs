using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

public interface IRemoteScanner
{
    Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(
        string remoteRoot, PathMapper pathMapper, ExclusionMatcher exclusions, CancellationToken cancellationToken = default);
}

/// <summary>
/// BFS over the remote tree via repeated `filesystem list` calls — the CLI has no recursive
/// listing (docs/PLAN-LOCAL-SYNC.md Appendix A #4). Processes one "wave" (depth level) of
/// folders at a time, bounded by <paramref name="maxConcurrency"/>, which also happens to
/// match the plan's folder-creation-shallowest-first ordering for free. Each CLI call costs
/// ~3.5s of process-startup overhead regardless of folder size (Appendix A #11a) — keep this
/// scanner's concurrency and the caller's polling interval in mind together; this is the
/// component that most needs the "cache unchanged subtrees" optimization the appendix flags,
/// not yet implemented here.
/// </summary>
public sealed class RemoteScanner : IRemoteScanner
{
    private readonly ProtonDriveService _service;
    private readonly int _maxConcurrency;

    public RemoteScanner(ProtonDriveService service, int maxConcurrency = 3)
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
