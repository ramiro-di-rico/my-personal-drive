using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// Fetches Proton's live release manifest with the real code path. The unit tests pin the selection
/// rules against a captured copy; this one catches the thing a captured copy never can — Proton
/// changing the endpoint, the property names, or the channel name, which would silently turn every
/// update check into "could not reach the manifest".
///
/// Opt-in like the other integration tests, but note this one needs no CLI and no account: it only
/// reads a public static file.
/// </summary>
public class RealReleaseManifestTests
{
    [IntegrationFact]
    public async Task TheLiveManifest_StillParses_AndOffersABuildForThisMachine()
    {
        using var feed = new CliReleaseFeed();

        var release = await feed.GetLatestStableAsync();

        Assert.NotNull(release);
        Assert.True(CliVersionComparer.TryParse(release.Version, out _), $"Version '{release.Version}' is not a number this app can compare.");
        Assert.StartsWith("https://proton.me/download/drive/cli/", release.Url);
        // SHA-512 is 128 hex characters. A shorter value means the manifest changed shape and the
        // installer's verification would be comparing against something that isn't a hash.
        Assert.Equal(128, release.Sha512CheckSum.Length);
        Assert.Equal(CliPlatformKey.ForCurrentMachine(), release.Platform);
    }
}
