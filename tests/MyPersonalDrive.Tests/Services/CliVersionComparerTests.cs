using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The two sides speak different dialects: the CLI prints
/// <c>Proton Drive CLI cli-drive@0.6.0+f8e16aac</c> (real captured output) and the manifest gives a
/// bare <c>0.7.0</c>. These tests pin the lift-and-compare, and above all the refusal to guess.
/// </summary>
public class CliVersionComparerTests
{
    private const string RealInstalledLine = "Proton Drive CLI cli-drive@0.6.0+f8e16aac";

    [Fact]
    public void RealInstalledLine_AgainstANewerStable_IsAnUpdate()
        => Assert.Equal(CliUpdateAvailability.UpdateAvailable, CliVersionComparer.Compare(RealInstalledLine, "0.7.0"));

    [Fact]
    public void SameVersion_IsUpToDate()
        => Assert.Equal(CliUpdateAvailability.UpToDate, CliVersionComparer.Compare(RealInstalledLine, "0.6.0"));

    /// <summary>A locally built or pre-release CLI ahead of Stable must not be "updated" backwards.</summary>
    [Fact]
    public void InstalledAheadOfStable_IsNotOfferedADowngrade()
        => Assert.Equal(
            CliUpdateAvailability.UpToDate,
            CliVersionComparer.Compare("Proton Drive CLI cli-drive@0.9.0+abc", "0.7.0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("Unavailable: unknown flag: --version")]
    [InlineData("Proton Drive CLI cli-drive@notaversion")]
    public void AnUnreadableInstalledVersion_IsUnknown_NotAnUpdateOffer(string? installed)
        => Assert.Equal(CliUpdateAvailability.Unknown, CliVersionComparer.Compare(installed, "0.7.0"));

    [Fact]
    public void AnUnreadableManifestVersion_IsUnknown()
        => Assert.Equal(CliUpdateAvailability.Unknown, CliVersionComparer.Compare(RealInstalledLine, "latest"));

    [Fact]
    public void ParseInstalled_DropsBuildMetadataAfterThePlus()
        => Assert.Equal(new Version(0, 6, 0), CliVersionComparer.ParseInstalled(RealInstalledLine));

    /// <summary>
    /// The SDK line from the same `--version` output also contains an <c>@</c> token. Feeding it in
    /// must yield the SDK's own number rather than silently mixing the two — which is precisely why
    /// the service only ever passes the first line.
    /// </summary>
    [Fact]
    public void ParseInstalled_OnTheSdkLine_ReadsTheSdkNumber()
        => Assert.Equal(new Version(0, 19, 2), CliVersionComparer.ParseInstalled("Proton Drive SDK js@0.19.2+f8e16aac"));

    [Fact]
    public void TryParse_RejectsABareMajor()
        => Assert.False(CliVersionComparer.TryParse("1", out _));
}
