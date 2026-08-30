namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Compares the version the installed CLI reports with the one the release manifest offers.
///
/// The two sides do not speak the same dialect. `proton-drive --version` prints (real captured
/// output from the installed binary):
///
/// <code>
/// Proton Drive CLI cli-drive@0.6.0+f8e16aac
/// </code>
///
/// while the manifest gives a bare <c>0.7.0</c>. So the numeric part has to be lifted out of the
/// CLI's line before anything can be compared. Everything after <c>+</c> is build metadata and is
/// ignored, which is also what semver says to do.
///
/// The bias is deliberate: when the installed version can't be understood, this reports
/// <see cref="CliUpdateAvailability.Unknown"/> rather than guessing. Guessing wrong in the
/// optimistic direction would offer to overwrite a working CLI with an older build.
/// </summary>
internal static class CliVersionComparer
{
    /// <summary>
    /// Pulls the numeric version out of a `--version` line. Returns null when there is no
    /// <c>name@version</c> token, or its numeric part doesn't parse.
    /// </summary>
    internal static Version? ParseInstalled(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine))
        {
            return null;
        }

        foreach (var token in versionLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = token.IndexOf('@');
            if (at < 0 || at == token.Length - 1)
            {
                continue;
            }

            if (TryParse(token[(at + 1)..], out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>Parses a manifest version (a bare <c>0.7.0</c>).</summary>
    internal static bool TryParse(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Drop semver build metadata and pre-release tags: 0.7.0+abc / 0.7.0-rc1 both compare as
        // 0.7.0 here. Ordering pre-releases correctly is not something this app needs, and the
        // manifest's Stable channel does not publish them.
        var numeric = value.Trim();
        var cut = numeric.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            numeric = numeric[..cut];
        }

        // Version.TryParse rejects a bare "1", which is not a shape either side emits, and accepts
        // the 2-to-4 component forms that they do.
        return Version.TryParse(numeric, out version);
    }

    public static CliUpdateAvailability Compare(string? installedVersionLine, string? availableVersion)
    {
        var installed = ParseInstalled(installedVersionLine);
        if (installed is null || !TryParse(availableVersion, out var available) || available is null)
        {
            return CliUpdateAvailability.Unknown;
        }

        return available > installed
            ? CliUpdateAvailability.UpdateAvailable
            : CliUpdateAvailability.UpToDate;
    }
}

public enum CliUpdateAvailability
{
    /// <summary>Either side was unreadable; the app must not offer an install on this.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable
}
