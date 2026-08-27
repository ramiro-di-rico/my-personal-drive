using System.Collections.Frozen;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// OneDrive's path and naming rules — docs/PLAN-CLOUD-PROVIDERS.md §4.6 (O6). The reserved-name
/// list is per Microsoft's published OneDrive naming restrictions; <b>unverified against the live
/// service</b> per that section's own caveat — confirm before relying on it for anything beyond
/// this app's own skip-with-reason behavior (a name this list wrongly allows still fails cleanly
/// as a Graph 400, mapped to <see cref="DriveErrorKind.InvalidArgument"/>; it just isn't skipped
/// pre-emptively).
/// </summary>
public sealed class OneDrivePathSyntax : IProviderPathSyntax
{
    private static readonly char[] ReservedCharacters = ['"', '*', ':', '<', '>', '?', '/', '\\', '|'];

    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        ".lock", "CON", "PRN", "AUX", "NUL", "desktop.ini",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public string Combine(string parentPath, string name)
        => string.IsNullOrEmpty(parentPath) || parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";

    /// <summary>Only '/' makes a remote name unrepresentable as a single local path segment — same rule as Proton's today.</summary>
    public bool IsRemoteNameMappableLocally(string name) => !name.Contains('/');

    public bool IsLocalNameMappableRemotely(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.IndexOfAny(ReservedCharacters) >= 0)
        {
            return false;
        }

        if (name[0] == ' ' || name[^1] == ' ' || name[^1] == '.')
        {
            return false;
        }

        if (name.StartsWith("~$", StringComparison.Ordinal))
        {
            return false;
        }

        return !ReservedNames.Contains(name);
    }

    /// <summary>OneDrive names are case-insensitive but case-preserving — unlike Proton/Linux, which are both case-sensitive.</summary>
    public StringComparison Comparison => StringComparison.OrdinalIgnoreCase;
}
