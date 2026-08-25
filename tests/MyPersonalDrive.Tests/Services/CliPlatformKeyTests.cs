using System.Runtime.InteropServices;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Picking the wrong platform key downloads a binary that fails at exec time rather than at
/// download time, so the glibc/musl split is worth pinning. These assertions are OS-conditional
/// because <see cref="CliPlatformKey"/> reads the real <see cref="OperatingSystem"/>.
/// </summary>
public class CliPlatformKeyTests
{
    [Fact]
    public void OnLinux_AMuslRuntimeIdentifier_SelectsTheMuslBuild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal("linux/x64-musl", CliPlatformKey.Resolve("linux-musl-x64", Architecture.X64));
        Assert.Equal("linux/arm64-musl", CliPlatformKey.Resolve("linux-musl-arm64", Architecture.Arm64));
    }

    [Fact]
    public void OnLinux_AGlibcRuntimeIdentifier_SelectsThePlainBuild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal("linux/x64", CliPlatformKey.Resolve("linux-x64", Architecture.X64));
        Assert.Equal("linux/arm64", CliPlatformKey.Resolve("linux-arm64", Architecture.Arm64));
    }

    /// <summary>
    /// An architecture the manifest has no build for must resolve to null, so the feed reports
    /// "nothing published for this platform" instead of installing something that cannot run.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void AnUnsupportedArchitecture_HasNoKey(Architecture architecture)
        => Assert.Null(CliPlatformKey.Resolve("linux-x64", architecture));

    [Fact]
    public void TheRunningMachine_ResolvesToAKeyTheManifestPublishes()
    {
        var key = CliPlatformKey.ForCurrentMachine();

        // The suite runs on linux-x64 in this repo; the point is that the real runtime information
        // produces one of the manifest's own keys rather than something invented.
        Assert.Contains(key, (string?[])["linux/x64", "linux/x64-musl", "linux/arm64", "linux/arm64-musl", "macos/x64", "macos/arm64", "windows/x64", "windows/arm64"]);
    }
}
