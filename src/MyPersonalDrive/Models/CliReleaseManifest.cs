using System.Text.Json.Serialization;

namespace MyPersonalDrive.Models;

/// <summary>
/// The release manifest Proton publishes for the Drive CLI, at
/// <c>https://proton.me/download/drive/cli/version.json</c>. Real captured shape:
///
/// <code>
/// {
///   "Releases": [
///     {
///       "CategoryName": "Stable",
///       "Version": "0.7.0",
///       "ReleaseDate": "2026-07-31",
///       "Files": [
///         {
///           "Url": "https://proton.me/download/drive/cli/0.7.0/linux-x64/proton-drive",
///           "Sha512CheckSum": "5a5affc…",
///           "Platform": "linux/x64"
///         }
///       ]
///     }
///   ]
/// }
/// </code>
///
/// The PascalCase property names are the manifest's own — no naming policy is applied, so these
/// names have to match it exactly. The SHA-512 values were cross-checked against the ones shown
/// on the human-facing download page, so the manifest is the same source of truth the page uses.
/// </summary>
public sealed class CliReleaseManifest
{
    [JsonPropertyName("Releases")]
    public List<CliRelease> Releases { get; set; } = [];
}

public sealed class CliRelease
{
    [JsonPropertyName("CategoryName")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("ReleaseDate")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("Files")]
    public List<CliReleaseFile> Files { get; set; } = [];
}

public sealed class CliReleaseFile
{
    [JsonPropertyName("Url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("Sha512CheckSum")]
    public string Sha512CheckSum { get; set; } = string.Empty;

    /// <summary>Manifest platform key, e.g. <c>linux/x64</c> or <c>linux/x64-musl</c>.</summary>
    [JsonPropertyName("Platform")]
    public string Platform { get; set; } = string.Empty;
}

/// <summary>
/// A stable release resolved down to the single file this machine should install, so callers never
/// have to re-do the platform match.
/// </summary>
public sealed record CliReleaseCandidate(string Version, string ReleaseDate, string Url, string Sha512CheckSum, string Platform);
