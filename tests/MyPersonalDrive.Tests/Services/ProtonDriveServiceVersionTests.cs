using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// `proton-drive --version` prints the CLI version and then the bundled SDK version (real output
/// captured in <see cref="ProtonDriveService.GetCliVersionAsync"/>'s docs). The service returns the
/// first line verbatim and parses nothing, so what is pinned here is that contract: the arguments
/// sent, the SDK line dropped, and no assumed format for the rest.
/// </summary>
public class ProtonDriveServiceVersionTests
{
    private static (ProtonDriveService Service, FakeCliExecutor Executor) CreateSut()
    {
        var executor = new FakeCliExecutor();
        return (new ProtonDriveService(executor), executor);
    }

    [Fact]
    public async Task GetCliVersion_SendsTheVersionFlagAsItsOwnArgument()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("proton-drive 1.2.3");

        await service.GetCliVersionAsync();

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["--version"], call.Arguments);
    }

    [Fact]
    public async Task GetCliVersion_ReturnsWhateverTheCliPrinted_Untouched()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("some-unexpected-wording v9\n");

        Assert.Equal("some-unexpected-wording v9", await service.GetCliVersionAsync());
    }

    [Fact]
    /// <summary>Real captured output — the SDK line must not be what the user sees as "the CLI version".</summary>
    public async Task GetCliVersion_RealOutput_KeepsTheCliLineAndDropsTheSdkLine()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("Proton Drive CLI cli-drive@0.6.0+f8e16aac\nProton Drive SDK js@0.19.2+f8e16aac\n");

        Assert.Equal("Proton Drive CLI cli-drive@0.6.0+f8e16aac", await service.GetCliVersionAsync());
    }

    [Fact]
    public async Task GetCliVersion_EmptyOutput_IsNullRatherThanAnEmptyVersion()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("   \n\n");

        Assert.Null(await service.GetCliVersionAsync());
    }
}
