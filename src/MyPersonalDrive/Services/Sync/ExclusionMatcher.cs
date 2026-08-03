using System.Text.RegularExpressions;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Decides whether a relative path should be skipped by the local scanner. See
/// docs/PLAN-LOCAL-SYNC.md §6.1 for the default exclusion list. A pattern ending in <c>/</c>
/// excludes a directory (by name, at any depth) and everything under it; anything else is a
/// glob matched against the final path segment's file name.
/// </summary>
public sealed class ExclusionMatcher
{
    private static readonly string[] DefaultExcludedDirectoryNames =
        [".git", "node_modules", ".mypersonaldrive-trash", ".mypersonaldrive-tmp"];

    private static readonly string[] DefaultExcludedFileGlobs =
        [".DS_Store", "Thumbs.db", "*.tmp", "*.swp", "~$*"];

    private readonly HashSet<string> _excludedDirectoryNames = new(StringComparer.Ordinal);
    private readonly List<Regex> _excludedFilePatterns = [];

    public ExclusionMatcher(IEnumerable<string>? extraGlobs = null)
    {
        foreach (var name in DefaultExcludedDirectoryNames)
        {
            _excludedDirectoryNames.Add(name);
        }

        foreach (var glob in DefaultExcludedFileGlobs)
        {
            _excludedFilePatterns.Add(CompileGlob(glob));
        }

        foreach (var glob in extraGlobs ?? [])
        {
            if (glob.EndsWith('/'))
            {
                _excludedDirectoryNames.Add(glob.TrimEnd('/'));
            }
            else
            {
                _excludedFilePatterns.Add(CompileGlob(glob));
            }
        }
    }

    /// <param name="relativePath">`/`-separated, no leading slash — see PathMapper's convention.</param>
    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(_excludedDirectoryNames.Contains))
        {
            return true;
        }

        if (isDirectory)
        {
            return false;
        }

        var fileName = segments[^1];
        return _excludedFilePatterns.Any(pattern => pattern.IsMatch(fileName));
    }

    private static Regex CompileGlob(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
