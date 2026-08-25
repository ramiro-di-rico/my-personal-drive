using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Breadth-first walk of the remote tree via repeated <c>filesystem list</c> calls. Extracted from
/// <c>Services.Sync.RemoteScanner</c> so the sync engine and the folder-metrics scanner share one
/// implementation — and, more importantly, one set of reasons for how it is shaped.
///
/// <b>Why BFS at all.</b> The CLI has no recursive listing (docs/PLAN-LOCAL-SYNC.md Appendix A #4),
/// so a subtree costs one process launch per folder. Processing a whole depth level ("wave") at a
/// time bounds the concurrency and, for the sync engine, happens to produce the
/// shallowest-folders-first ordering its folder creation needs.
///
/// <b>Why concurrency is safe.</b> Concurrent <c>proton-drive</c> processes really do crash on the
/// CLI's own SQLite cache with <c>SQLITE_BUSY</c>. Appendix A #16 established that the contention is
/// entirely over the one shared cache file, and <see cref="ProtonDriveCliExecutor"/> gives each
/// concurrent process a private <c>XDG_CACHE_HOME</c> — measured at 64 clean calls out of 64 with
/// eight in flight, against 15 failures in 64 sharing one cache. The executor is what makes this
/// safe; the width here only says how many the walk is allowed to ask for.
///
/// Each call costs ~3.5 s of Node.js startup regardless of folder size (Appendix A #11a) and
/// subtree caching is impossible with this CLI (#11b), so a walk is unavoidably
/// O(folders) × 3.5 s / concurrency. Callers that a user is waiting on must report progress and
/// accept cancellation.
/// </summary>
public sealed class RemoteTreeWalker
{
    private readonly ProtonDriveService _service;
    private readonly int _maxConcurrency;

    /// <param name="maxConcurrency">
    /// 0 defers to a ceiling derived from the CPU count: the ~3.5 s per call is process startup, so
    /// cores are the real limit.
    /// </param>
    public RemoteTreeWalker(ProtonDriveService service, int maxConcurrency = 0)
    {
        _service = service;
        _maxConcurrency = maxConcurrency > 0
            ? maxConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 8);
    }

    /// <summary>
    /// Walks <paramref name="rootPath"/> depth level by depth level, invoking
    /// <paramref name="onNode"/> for every node found. <paramref name="onNode"/> returns whether to
    /// descend into that node — the caller decides what "excluded" means, and a folder it refuses
    /// costs nothing further.
    /// </summary>
    /// <param name="onWaveCompleted">
    /// Called after each depth level with (folders visited so far, folders queued for the next
    /// level). Progress on this walk cannot be a percentage: BFS does not know the total until it is
    /// finished, so callers report counts.
    /// </param>
    public async Task WalkAsync(
        string rootPath,
        Func<DriveItem, bool> onNode,
        Action<int, int>? onWaveCompleted = null,
        CancellationToken cancellationToken = default)
    {
        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        var currentWave = new List<string> { rootPath };
        var foldersVisited = 0;

        while (currentWave.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listings = await Task.WhenAll(
                currentWave.Select(folderPath => ListOneFolderAsync(folderPath, semaphore, cancellationToken)));

            foldersVisited += currentWave.Count;

            var nextWave = new List<string>();
            foreach (var items in listings)
            {
                foreach (var item in items)
                {
                    if (onNode(item) && item.IsFolder)
                    {
                        nextWave.Add(item.Path);
                    }
                }
            }

            onWaveCompleted?.Invoke(foldersVisited, nextWave.Count);
            currentWave = nextWave;
        }
    }

    private async Task<IReadOnlyList<DriveItem>> ListOneFolderAsync(string folderPath, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            // Checked again after the wait, not just at the top of each wave: a wide wave queues
            // more folders than the semaphore admits at once, and every one of those is a ~3.5 s
            // process the user has already asked us not to start.
            cancellationToken.ThrowIfCancellationRequested();
            return await _service.LoadFolderAsync(folderPath, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
