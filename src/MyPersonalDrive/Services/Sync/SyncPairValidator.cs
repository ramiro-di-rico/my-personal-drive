using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The checks from docs/PLAN-LOCAL-SYNC.md §12 that need no IO, so they can be tested exhaustively:
/// path shape, the refuse-to-sync-your-whole-home rule, and overlap against pairs that already
/// exist. Returns the message to show the user, or null when the pair is acceptable.
/// </summary>
public static class SyncPairValidator
{
    public static string? Validate(string remotePath, string localPath, IReadOnlyList<SyncPair> existingPairs)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
        {
            return "The remote path must be an absolute path starting with '/'.";
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return "Choose a local folder.";
        }

        var trimmedLocal = localPath.TrimEnd('/', '\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmedLocal.Length == 0 || trimmedLocal == "/" || string.Equals(trimmedLocal, home.TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            return "Refusing to sync your entire home directory or the filesystem root — pick a specific subfolder.";
        }

        return FindOverlap(remotePath, localPath, existingPairs);
    }

    /// <summary>
    /// Rejects a pair whose local or remote scope overlaps an existing one.
    ///
    /// <b>Overlapping local folders are actively destructive</b>, not merely redundant. Take
    /// <c>~/A ↔ /my-files/X</c> and <c>~/A/Sub ↔ /my-files/Y</c>: the first pair's scanner walks
    /// <c>~/A/Sub</c> too, its own remote root has no <c>Sub</c>, so it concludes the folder was
    /// deleted remotely and moves it to the local trash — which the second pair then downloads
    /// again, forever.
    ///
    /// <b>Overlapping remote folders break echo suppression</b>, which is keyed per pair
    /// (<see cref="SyncEchoSuppressor"/>). Pair A has no idea pair B just trashed something, so it
    /// sees the node still listed (Appendix A #15's stale listing), reads it as "new remotely", and
    /// downloads back what the other pair deleted — exactly the resurrection bug that fix removed,
    /// reintroduced across pairs.
    /// </summary>
    private static string? FindOverlap(string remotePath, string localPath, IReadOnlyList<SyncPair> existingPairs)
    {
        var newLocal = NormalizeLocal(localPath);
        var newRemote = NormalizeRemote(remotePath);

        foreach (var pair in existingPairs)
        {
            var existingLocal = NormalizeLocal(pair.LocalPath);
            if (Overlaps(newLocal, existingLocal, Path.DirectorySeparatorChar))
            {
                return string.Equals(newLocal, existingLocal, StringComparison.Ordinal)
                    ? $"'{pair.LocalPath}' is already synced with '{pair.RemotePath}'."
                    : $"That local folder overlaps '{pair.LocalPath}', which is already synced with " +
                      $"'{pair.RemotePath}'. Two pairs sharing a folder would each treat the other's files as " +
                      "deletions. Pick a folder outside it.";
            }

            var existingRemote = NormalizeRemote(pair.RemotePath);
            if (Overlaps(newRemote, existingRemote, '/'))
            {
                return string.Equals(newRemote, existingRemote, StringComparison.Ordinal)
                    ? $"'{pair.RemotePath}' is already synced with '{pair.LocalPath}'."
                    : $"That remote folder overlaps '{pair.RemotePath}', which is already synced with " +
                      $"'{pair.LocalPath}'. Two pairs covering the same remote subtree can undo each other's " +
                      "deletions. Pick a folder outside it.";
            }
        }

        return null;
    }

    /// <summary>
    /// Same path, or one inside the other. The separator check is what keeps <c>/a/bc</c> from
    /// counting as nested inside <c>/a/b</c>.
    /// </summary>
    private static bool Overlaps(string first, string second, char separator)
        => string.Equals(first, second, StringComparison.Ordinal)
           || first.StartsWith(second + separator, StringComparison.Ordinal)
           || second.StartsWith(first + separator, StringComparison.Ordinal);

    /// <summary>
    /// Resolves <c>.</c>, <c>..</c> and redundant separators so two spellings of one folder compare
    /// equal. Ordinal (case-sensitive) because Linux is; on a case-insensitive filesystem two
    /// differently-cased spellings of the same folder would slip through, which is a gap worth
    /// noting if this app ever ships for Windows or macOS.
    /// </summary>
    private static string NormalizeLocal(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('/', '\\');
        }
    }

    private static string NormalizeRemote(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
