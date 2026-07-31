using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

public interface ILocalScanner
{
    Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(string rootPath, ExclusionMatcher exclusions, CancellationToken cancellationToken = default);
}

/// <summary>
/// Enumerates a local directory tree into <see cref="NodeFingerprint"/>s, keyed by the
/// relative path (see <see cref="PathMapper"/>'s convention). Stat-only: never opens file
/// contents, so <see cref="NodeFingerprint.ContentHash"/> is always null here — hashing is a
/// separate, selective step (see <see cref="LocalFileHasher"/>). See docs/PLAN-LOCAL-SYNC.md §6.1.
/// </summary>
public sealed class LocalScanner : ILocalScanner
{
    /// <summary>
    /// Files modified more recently than this are skipped for this scan cycle — likely still
    /// being written by another process. They'll be picked up once they've settled.
    /// </summary>
    private static readonly TimeSpan MinAgeBeforeIncluding = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyDictionary<string, NodeFingerprint>> ScanAsync(string rootPath, ExclusionMatcher exclusions, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(rootPath, exclusions, cancellationToken), cancellationToken);

    private static IReadOnlyDictionary<string, NodeFingerprint> Scan(string rootPath, ExclusionMatcher exclusions, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, NodeFingerprint>();
        if (!Directory.Exists(rootPath))
        {
            return result;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);
        var now = DateTimeOffset.UtcNow;

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentDirectory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Can't read this directory (permissions, or it vanished mid-scan) — skip it,
                // never abort the whole scan over one bad subtree.
                continue;
            }

            foreach (var entryPath in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessEntry(rootPath, entryPath, exclusions, now, result, pendingDirectories);
            }
        }

        return result;
    }

    private static void ProcessEntry(
        string rootPath, string entryPath, ExclusionMatcher exclusions, DateTimeOffset now,
        Dictionary<string, NodeFingerprint> result, Stack<string> pendingDirectories)
    {
        bool isDirectory;
        try
        {
            var attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return; // never follow symlinks (avoids cycles)
            }

            isDirectory = (attributes & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var relativePath = Path.GetRelativePath(rootPath, entryPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        if (exclusions.IsExcluded(relativePath, isDirectory))
        {
            return;
        }

        if (isDirectory)
        {
            result[relativePath] = new NodeFingerprint(relativePath, IsFolder: true, Size: null, ModifiedAt: SafeGetLastWriteTime(entryPath), NodeId: null, ContentHash: null);
            pendingDirectories.Push(entryPath);
            return;
        }

        long size;
        DateTimeOffset modifiedAt;
        try
        {
            var info = new FileInfo(entryPath);
            size = info.Length;
            modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        if (now - modifiedAt < MinAgeBeforeIncluding)
        {
            return;
        }

        result[relativePath] = new NodeFingerprint(relativePath, IsFolder: false, size, modifiedAt, NodeId: null, ContentHash: null);
    }

    private static DateTimeOffset? SafeGetLastWriteTime(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
