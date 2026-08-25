using System.Net.Http;
using System.Text.Json;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Fetches <c>https://proton.me/download/drive/cli/version.json</c> — the same manifest that backs
/// Proton's human-facing download page (the SHA-512 values match), and the only machine-readable
/// source for "is there a newer CLI". The CLI itself has no <c>update</c> or <c>self-update</c>
/// subcommand, verified against <c>--help</c> on cli-drive@0.6.0.
///
/// <b>This is the app's only outbound network call.</b> Everything else goes through the CLI
/// process. Keep it that way: this class talks to a static file, not to the Drive API.
/// </summary>
public sealed class CliReleaseFeed : ICliReleaseFeed, IDisposable
{
    public const string ManifestUrl = "https://proton.me/download/drive/cli/version.json";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string? _platformKey;

    public CliReleaseFeed(HttpClient? httpClient = null, string? platformKey = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _platformKey = platformKey ?? CliPlatformKey.ForCurrentMachine();
    }

    public async Task<CliReleaseCandidate?> GetLatestStableAsync(CancellationToken cancellationToken = default)
    {
        if (_platformKey is null)
        {
            return null;
        }

        var json = await _httpClient.GetStringAsync(ManifestUrl, cancellationToken);
        return SelectStable(json, _platformKey);
    }

    /// <summary>
    /// Picks the Stable release and, within it, the file built for <paramref name="platformKey"/>.
    /// Separate from the HTTP call so the selection rules can be tested against the real captured
    /// manifest without a network round-trip. Throws <see cref="JsonException"/> on malformed JSON —
    /// callers surface that rather than silently reporting "no update".
    /// </summary>
    internal static CliReleaseCandidate? SelectStable(string json, string platformKey)
    {
        var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.CliReleaseManifest);
        if (manifest is null)
        {
            return null;
        }

        // Only Stable. A Beta/EarlyAccess category showing up later must not silently become the
        // version the app offers to install over a working CLI.
        var release = manifest.Releases.FirstOrDefault(
            r => string.Equals(r.CategoryName, "Stable", StringComparison.OrdinalIgnoreCase));
        if (release is null || string.IsNullOrWhiteSpace(release.Version))
        {
            return null;
        }

        var file = release.Files.FirstOrDefault(
            f => string.Equals(f.Platform, platformKey, StringComparison.OrdinalIgnoreCase));
        if (file is null || string.IsNullOrWhiteSpace(file.Url) || string.IsNullOrWhiteSpace(file.Sha512CheckSum))
        {
            return null;
        }

        return new CliReleaseCandidate(release.Version, release.ReleaseDate, file.Url, file.Sha512CheckSum, file.Platform);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
