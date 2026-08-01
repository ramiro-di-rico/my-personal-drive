using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B1.3: arguments must reach the process as a list (so the
/// runtime escapes them per platform), never as a pre-joined string. These tests assert on the
/// argument list itself, which is only meaningful because IProtonDriveCliExecutor takes
/// IReadOnlyList&lt;string&gt; instead of a single string.
/// </summary>
public class ProtonDriveServiceCommandTests
{
    private static (ProtonDriveService Service, FakeCliExecutor Executor) CreateSut()
    {
        var executor = new FakeCliExecutor();
        return (new ProtonDriveService(executor), executor);
    }

    [Fact]
    public async Task LoadFolder_NameWithQuotesAndSpaces_IsPassedAsOneArgument()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("[]");

        await service.LoadFolderAsync("""/my-files/My "quoted" folder""");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "list", "--json", """/my-files/My "quoted" folder"""], call.Arguments);
    }

    [Fact]
    public async Task Download_PassesPathAndLocalFolderAsSeparateArguments()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.DownloadFileAsync("/my-files/report.pdf", "/home/user/Downloads");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "download", "/my-files/report.pdf", "/home/user/Downloads"], call.Arguments);
    }

    [Fact]
    public async Task Move_SingleItem_PutsTheTargetParentLast()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.MoveItemAsync("/my-files/a.txt", "/my-files/target");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "move", "/my-files/a.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Move_MultipleItems_PassesEverySourceBeforeTheTarget()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.MoveItemsAsync(["/my-files/a.txt", "/my-files/b.txt"], "/my-files/target");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "move", "/my-files/a.txt", "/my-files/b.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Move_WithNoSources_ThrowsWithoutInvokingTheCli()
    {
        var (service, executor) = CreateSut();

        await Assert.ThrowsAsync<ArgumentException>(() => service.MoveItemsAsync([], "/my-files/target"));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Copy_WithoutNewName_OmitsTheNameFlag()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.CopyItemAsync("/my-files/a.txt", "/my-files/target");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "copy", "/my-files/a.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Copy_WithNewName_IncludesTheNameFlagAsItsOwnArgument()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.CopyItemAsync("/my-files/a.txt", "/my-files/target", "b.txt");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "copy", "-n", "b.txt", "/my-files/a.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Upload_MultipleFiles_WithConflictStrategy()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.UploadFilesAsync(["/local/a.txt", "/local/b.txt"], "/my-files/target", UploadConflictStrategy.KeepBoth);

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "upload", "-c", "keep-both", "/local/a.txt", "/local/b.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Upload_WithNoConflictStrategy_OmitsTheFlag()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.UploadFilesAsync(["/local/a.txt"], "/my-files/target");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "upload", "/local/a.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Authenticate_UsesAnInfiniteTimeout()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.AuthenticateAsync();

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["auth", "login"], call.Arguments);
        Assert.Equal(Timeout.InfiniteTimeSpan, call.Timeout);
    }

    [Fact]
    public async Task CreateFolder_PassesParentAndNameSeparately()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.CreateFolderAsync("/my-files", "New Folder");

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "create-folder", "/my-files", "New Folder"], call.Arguments);
    }
}
