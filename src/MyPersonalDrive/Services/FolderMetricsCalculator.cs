using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Aggregates a listing into <see cref="FolderMetrics"/>. Pure, in-memory, and free: the children
/// are already loaded and every field it reads is already on <see cref="DriveItem"/>, so this runs
/// on every folder load without touching the CLI (docs/PLAN-BROWSER-VIEWS.md M2).
/// </summary>
public static class FolderMetricsCalculator
{
    public const int LargestItemCount = 5;

    public static FolderMetrics FromChildren(string path, IReadOnlyList<DriveItem> children, DateTimeOffset now)
    {
        if (children.Count == 0)
        {
            return FolderMetrics.Empty(path, now);
        }

        var fileCount = 0;
        var folderCount = 0;
        var totalSize = 0L;
        var unknownSizeCount = 0;
        DateTimeOffset? newest = null;
        DateTimeOffset? oldest = null;
        var buckets = new Dictionary<FileKind, (int Count, long TotalSize)>();

        foreach (var child in children)
        {
            if (child.IsFolder)
            {
                folderCount++;
            }
            else
            {
                fileCount++;
                if (child.Size is { } size)
                {
                    totalSize += size;
                }
                else
                {
                    unknownSizeCount++;
                }
            }

            if (child.ModifiedAt is { } modifiedAt)
            {
                newest = newest is null || modifiedAt > newest ? modifiedAt : newest;
                oldest = oldest is null || modifiedAt < oldest ? modifiedAt : oldest;
            }

            // Folders are bucketed too: "12 carpetas" belongs in the same histogram, and leaving
            // them out would make the counts not add up to what the listing shows.
            var kind = FileKindClassifier.Classify(child.Name, child.IsFolder);
            var existing = buckets.GetValueOrDefault(kind);
            buckets[kind] = (existing.Count + 1, existing.TotalSize + (child.Size ?? 0));
        }

        // Biggest first, because that's the question the histogram answers ("what is filling this
        // up"). Count then kind name break ties so the order is stable across recomputations —
        // a histogram that reshuffles on every refresh reads as broken.
        var orderedBuckets = buckets
            .Select(entry => new FolderKindBucket(entry.Key, entry.Value.Count, entry.Value.TotalSize))
            .OrderByDescending(bucket => bucket.TotalSize)
            .ThenByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.Kind.ToString(), StringComparer.Ordinal)
            .ToList();

        var largest = children
            .Where(child => !child.IsFolder && child.Size is > 0)
            .OrderByDescending(child => child.Size)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Take(LargestItemCount)
            .ToList();

        return new FolderMetrics(
            Path: path,
            IsDeep: false,
            IsComplete: true,
            FileCount: fileCount,
            FolderCount: folderCount,
            TotalSize: totalSize,
            UnknownSizeCount: unknownSizeCount,
            Buckets: orderedBuckets,
            LargestItems: largest,
            NewestModifiedAt: newest,
            OldestModifiedAt: oldest,
            ScannedFolderCount: 1,
            ComputedAt: now);
    }
}
