using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services;

/// <summary>
/// Recursive metrics for one remote folder: what the whole subtree contains and weighs
/// (docs/PLAN-BROWSER-VIEWS.md M3).
///
/// <b>This is expensive and that shapes everything.</b> One <c>filesystem list</c> per folder at
/// ~3.5 s each (PLAN-LOCAL-SYNC Appendix A #11a); a 500-folder subtree is minutes, not seconds. So
/// it is only ever user-initiated, it reports progress, it accepts cancellation at every wave, and a
/// cancelled scan still returns what it managed to aggregate — marked
/// <see cref="FolderMetrics.IsComplete"/> false, so nothing downstream can mistake a partial answer
/// for a finished one.
///
/// Unlike <c>Services.Sync.RemoteScanner</c> this does <b>not</b> reset the CLI's own cache first.
/// The sync engine needs that guarantee because a missed node reads as a deletion; here a slightly
/// stale byte count is harmless, and paying a cold start per folder would make an already slow
/// operation slower — including for the user's next navigation.
/// </summary>
public sealed class FolderStatsScanner
{
    private readonly RemoteTreeWalker _walker;
    private readonly TimeProvider _timeProvider;

    public FolderStatsScanner(ICloudDriveProvider provider, TimeProvider? timeProvider = null, int maxConcurrency = 0)
    {
        _walker = new RemoteTreeWalker(provider, maxConcurrency);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FolderMetrics> ScanAsync(
        string remotePath,
        IProgress<FolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var aggregate = new Aggregate();
        var isComplete = true;

        try
        {
            await _walker.WalkAsync(
                remotePath,
                item =>
                {
                    aggregate.Add(item);
                    return true;
                },
                (visited, queued) =>
                {
                    aggregate.FoldersVisited = visited;
                    progress?.Report(new FolderScanProgress(visited, queued));
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown. The user cancelled a scan they had already waited minutes
            // for; handing back the partial aggregate lets the UI show "so far" instead of throwing
            // that work away. The caller distinguishes the two through IsComplete.
            isComplete = false;
        }

        return aggregate.ToMetrics(remotePath, isComplete, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Running totals. Holds no per-file list beyond the top-N: a full-drive scan can cross hundreds
    /// of thousands of nodes, and keeping them all to sort at the end would trade a slow operation
    /// for a slow operation that also exhausts memory.
    /// </summary>
    private sealed class Aggregate
    {
        private readonly Dictionary<FileKind, (int Count, long TotalSize)> _buckets = new();
        private readonly List<DriveItem> _largest = new();

        public int FoldersVisited { get; set; }

        private int _fileCount;
        private int _folderCount;
        private long _totalSize;
        private int _unknownSizeCount;
        private DateTimeOffset? _newest;
        private DateTimeOffset? _oldest;

        public void Add(DriveItem item)
        {
            if (item.IsFolder)
            {
                _folderCount++;
            }
            else
            {
                _fileCount++;
                if (item.Size is { } size)
                {
                    _totalSize += size;
                    TrackLargest(item, size);
                }
                else
                {
                    _unknownSizeCount++;
                }
            }

            if (item.ModifiedAt is { } modifiedAt)
            {
                _newest = _newest is null || modifiedAt > _newest ? modifiedAt : _newest;
                _oldest = _oldest is null || modifiedAt < _oldest ? modifiedAt : _oldest;
            }

            var kind = FileKindClassifier.Classify(item.Name, item.IsFolder);
            var existing = _buckets.GetValueOrDefault(kind);
            _buckets[kind] = (existing.Count + 1, existing.TotalSize + (item.Size ?? 0));
        }

        private void TrackLargest(DriveItem item, long size)
        {
            if (size <= 0)
            {
                return;
            }

            if (_largest.Count == FolderMetricsCalculator.LargestItemCount
                && size <= (_largest[^1].Size ?? 0))
            {
                return;
            }

            var index = _largest.FindIndex(existing => size > (existing.Size ?? 0));
            _largest.Insert(index < 0 ? _largest.Count : index, item);
            if (_largest.Count > FolderMetricsCalculator.LargestItemCount)
            {
                _largest.RemoveAt(_largest.Count - 1);
            }
        }

        public FolderMetrics ToMetrics(string path, bool isComplete, DateTimeOffset computedAt)
        {
            var buckets = _buckets
                .Select(entry => new FolderKindBucket(entry.Key, entry.Value.Count, entry.Value.TotalSize))
                .OrderByDescending(bucket => bucket.TotalSize)
                .ThenByDescending(bucket => bucket.Count)
                .ThenBy(bucket => bucket.Kind.ToString(), StringComparer.Ordinal)
                .ToList();

            return new FolderMetrics(
                Path: path,
                IsDeep: true,
                IsComplete: isComplete,
                FileCount: _fileCount,
                FolderCount: _folderCount,
                TotalSize: _totalSize,
                UnknownSizeCount: _unknownSizeCount,
                Buckets: buckets,
                LargestItems: _largest.ToList(),
                NewestModifiedAt: _newest,
                OldestModifiedAt: _oldest,
                // The root itself counts as visited, which is what the walker's first wave reports.
                ScannedFolderCount: FoldersVisited,
                ComputedAt: computedAt);
        }
    }
}
