namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// The single place that converts between a relative path (the sync engine's internal
/// identity — `/`-separated, no leading slash, exactly as the node names come back from the
/// CLI, case-sensitive), a remote absolute path (rooted at the pair's <c>RemotePath</c>,
/// Posix-style per the CLI's own path convention), and a local absolute path (rooted at the
/// pair's <c>LocalPath</c>, OS-native separators). See docs/PLAN-LOCAL-SYNC.md §3.2's "golden
/// rule" — nothing outside this class should combine or split these paths ad hoc.
/// </summary>
/// <remarks>
/// Known limitation, not handled here: the CLI escapes a literal <c>/</c> inside a node name
/// with a backslash when building a path argument (`filesystem --help`: "Escape / in node
/// names with a backslash"). <see cref="ProtonDriveService.CombinePath"/> does not currently
/// apply that escaping, so a node whose real name contains a literal slash would round-trip
/// incorrectly through the CLI today. This was not observed in Appendix A's F0 testing and is
/// out of scope for the initial RemoteToLocal milestone; flagged here for whoever tackles
/// TwoWay rename support, since that's where it would first bite.
/// </remarks>
public sealed class PathMapper
{
    private readonly string _remoteRoot;
    private readonly string _localRoot;

    public PathMapper(string remoteRoot, string localRoot)
    {
        _remoteRoot = NormalizeRemoteRoot(remoteRoot);
        _localRoot = localRoot.TrimEnd('/', '\\');
    }

    /// <summary>The remote path for a relative path, e.g. `"sub/file.txt"` → `"/my-files/Docs/sub/file.txt"`.</summary>
    public string ToRemoteAbsolute(string relativePath)
        => relativePath.Length == 0 ? _remoteRoot : $"{_remoteRoot}/{relativePath}";

    /// <summary>The local path for a relative path, converting `/` to the OS separator.</summary>
    public string ToLocalAbsolute(string relativePath)
        => relativePath.Length == 0
            ? _localRoot
            : Path.Combine(_localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The relative path for a remote absolute path that must be under this mapper's remote
    /// root. Throws <see cref="ArgumentException"/> if it isn't — a path outside the pair's
    /// root reaching the reconciler would be a bug upstream (e.g. a scanner that walked past
    /// the pair boundary), not a case to silently tolerate.
    /// </summary>
    public string ToRelativeFromRemote(string remoteAbsolutePath)
    {
        if (remoteAbsolutePath == _remoteRoot)
        {
            return string.Empty;
        }

        var prefix = _remoteRoot == "/" ? "/" : _remoteRoot + "/";
        if (!remoteAbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{remoteAbsolutePath}' is not under the sync pair's remote root '{_remoteRoot}'.", nameof(remoteAbsolutePath));
        }

        return remoteAbsolutePath[prefix.Length..];
    }

    /// <summary>The relative path for a local absolute path that must be under this mapper's local root.</summary>
    public string ToRelativeFromLocal(string localAbsolutePath)
    {
        var relative = Path.GetRelativePath(_localRoot, localAbsolutePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"'{localAbsolutePath}' is not under the sync pair's local root '{_localRoot}'.", nameof(localAbsolutePath));
        }

        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string NormalizeRemoteRoot(string remoteRoot)
    {
        if (string.IsNullOrWhiteSpace(remoteRoot) || remoteRoot == "/")
        {
            return "/";
        }

        return remoteRoot.TrimEnd('/');
    }
}
