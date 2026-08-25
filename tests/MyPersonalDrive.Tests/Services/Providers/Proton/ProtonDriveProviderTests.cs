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
}
