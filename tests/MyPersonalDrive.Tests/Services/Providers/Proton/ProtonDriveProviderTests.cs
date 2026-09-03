using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.Proton;

/// <summary>
/// Covers the one piece of real logic <see cref="ProtonDriveProvider"/> adds on top of
/// <see cref="ProtonDriveService"/>: translating its three CLI-shaped events into the
/// provider-neutral <see cref="ProviderActivity"/> feed (docs/PLAN-CLOUD-PROVIDERS.md §2.6).
/// </summary>
public class ProtonDriveProviderTests
{
    [Fact]
    public async Task Listing_RaisesStartedThenFinished_WithTheCommandLineAsLabel()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");
        var provider = new ProtonDriveProvider(new ProtonDriveService(executor));
        var seen = new List<ProviderActivity>();
        provider.Activity += (_, activity) => seen.Add(activity);

        await provider.Operations.ListFolderAsync("/my-files");

        Assert.Equal(2, seen.Count);

        Assert.Equal(ActivityKind.Started, seen[0].Kind);
        Assert.Equal("filesystem list --json /my-files", seen[0].Label);
        Assert.Null(seen[0].Text);
        Assert.False(seen[0].IsError);
        Assert.Null(seen[0].ExitCode);

        Assert.Equal(ActivityKind.Finished, seen[1].Kind);
        Assert.Equal("filesystem list --json /my-files", seen[1].Label);
        Assert.False(seen[1].IsError);
        Assert.Equal(0, seen[1].ExitCode);
    }

    /// <summary>
    /// Proton's CLI has no command to generate a share link — Capabilities.SupportsShareLinks is
    /// false, and the UI is expected to gate on that rather than ever calling this, but the method
    /// itself still has to fail loudly (not silently return an empty/placeholder string) for the
    /// rare caller that doesn't check first.
    /// </summary>
    [Fact]
    public void Capabilities_DoNotSupportShareLinks()
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));

        Assert.False(provider.Capabilities.SupportsShareLinks);
    }

    [Fact]
    public async Task CreateShareLinkAsync_Throws()
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));

        await Assert.ThrowsAsync<DriveException>(() => provider.Operations.CreateShareLinkAsync("/my-files/report.pdf"));
    }
}
