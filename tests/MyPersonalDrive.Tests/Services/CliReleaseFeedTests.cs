using System.Text.Json;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Selection rules over Proton's real release manifest. The JSON below is the actual response from
/// <c>https://proton.me/download/drive/cli/version.json</c>, trimmed to three platforms; the
/// linux/x64 checksum is the real published one and matches what the human-facing download page
/// shows. Nothing here is invented, per the repo's "never invent CLI output shapes" rule — the same
/// rule applies to the vendor's manifest.
/// </summary>
public class CliReleaseFeedTests
{
    private const string RealManifest = """
    {
      "Releases": [
        {
          "CategoryName": "Stable",
          "Version": "0.7.0",
          "ReleaseDate": "2026-07-31",
          "Files": [
            {
              "Url": "https://proton.me/download/drive/cli/0.7.0/linux-x64/proton-drive",
              "Sha512CheckSum": "5a5affcbec04ea926a32d10e236c1342227f1b6d416cb797f88f943b2c4f1dcf53b5897a115f1c1aa9ce8ce92fd637e1c50bd223b04866577681f0584eccdbc6",
              "Platform": "linux/x64"
            },
            {
              "Url": "https://proton.me/download/drive/cli/0.7.0/linux-x64-musl/proton-drive",
              "Sha512CheckSum": "fb0e9bb12e18ff3f9c07b18be76e209b7aeedcae23f0e6953d8334ba6516fb6264575f5c1f42021673b7562f0b751d476d4b92c01bffb5e02167f7d1f35889cb",
              "Platform": "linux/x64-musl"
            },
            {
              "Url": "https://proton.me/download/drive/cli/0.7.0/windows-x64/proton-drive.exe",
              "Sha512CheckSum": "b38b465141af1b3fdad5730f55676cf2e8d8f5a57c42c54cdb6ff14f62e95846e3d50c0ddc8f3a5a2b049bcf7a721f7900cb541d253ae2d4379c86648b147dce",
              "Platform": "windows/x64"
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void SelectStable_PicksTheFileForTheRequestedPlatform()
    {
        var candidate = CliReleaseFeed.SelectStable(RealManifest, "linux/x64");

        Assert.NotNull(candidate);
        Assert.Equal("0.7.0", candidate.Version);
        Assert.Equal("2026-07-31", candidate.ReleaseDate);
        Assert.Equal("https://proton.me/download/drive/cli/0.7.0/linux-x64/proton-drive", candidate.Url);
        Assert.StartsWith("5a5affcbec04", candidate.Sha512CheckSum);
    }

    /// <summary>glibc and musl are different files; handing a musl box the glibc build fails at exec time.</summary>
    [Fact]
    public void SelectStable_DoesNotConfuseMuslWithGlibc()
    {
        var glibc = CliReleaseFeed.SelectStable(RealManifest, "linux/x64");
        var musl = CliReleaseFeed.SelectStable(RealManifest, "linux/x64-musl");

        Assert.NotNull(glibc);
        Assert.NotNull(musl);
        Assert.NotEqual(glibc.Url, musl.Url);
        Assert.EndsWith("linux-x64-musl/proton-drive", musl.Url);
    }

    [Fact]
    public void SelectStable_PlatformWithNoPublishedBuild_IsNullRatherThanAWrongFile()
        => Assert.Null(CliReleaseFeed.SelectStable(RealManifest, "linux/riscv64"));

    /// <summary>A future Beta channel must never become what the app offers to install.</summary>
    [Fact]
    public void SelectStable_IgnoresNonStableChannels()
    {
        const string betaOnly = """
        {
          "Releases": [
            {
              "CategoryName": "Beta",
              "Version": "0.8.0",
              "ReleaseDate": "2026-08-05",
              "Files": [
                { "Url": "https://proton.me/x", "Sha512CheckSum": "aa", "Platform": "linux/x64" }
              ]
            }
          ]
        }
        """;

        Assert.Null(CliReleaseFeed.SelectStable(betaOnly, "linux/x64"));
    }

    /// <summary>
    /// Malformed JSON must not read as "no update available" — that would silently hide a broken
    /// endpoint forever. The caller turns this into a visible message.
    /// </summary>
    [Fact]
    public void SelectStable_MalformedJson_Throws()
        => Assert.Throws<JsonException>(() => CliReleaseFeed.SelectStable("{ not json", "linux/x64"));

    [Fact]
    public void SelectStable_EntryMissingItsChecksum_IsRefused()
    {
        const string noChecksum = """
        {
          "Releases": [
            {
              "CategoryName": "Stable",
              "Version": "0.7.0",
              "ReleaseDate": "2026-07-31",
              "Files": [
                { "Url": "https://proton.me/x", "Sha512CheckSum": "", "Platform": "linux/x64" }
              ]
            }
          ]
        }
        """;

        Assert.Null(CliReleaseFeed.SelectStable(noChecksum, "linux/x64"));
    }
}
