using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
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
        Assert.Equal(["filesystem", "upload", "-f", "rename", "-d", "rename", "/local/a.txt", "/local/b.txt", "/my-files/target"], call.Arguments);
    }

    [Fact]
    public async Task Upload_WithReplaceStrategy_SendsBothConflictFlags()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.UploadFilesAsync(["/local/a.txt"], "/my-files/target", UploadConflictStrategy.Replace);

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "upload", "-f", "replace", "-d", "replace", "/local/a.txt", "/my-files/target"], call.Arguments);
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

    // ---- node names containing '/' (backlog B1; the CLI's own escaping rule) ----

    [Fact]
    public void CombinePath_EscapesASlashInsideTheNodesOwnName()
    {
        // `filesystem list --help`: "Escape / in node names with a backslash", example
        // /my-files/folder/foo\/bar. Unescaped, this path was indistinguishable from a folder
        // 'foo' containing 'bar', so every command aimed at the node failed with "Node not found".
        Assert.Equal("/my-files/foo\\/bar", ProtonDriveService.CombinePath("/my-files", "foo/bar"));
    }

    [Fact]
    public void CombinePath_LeavesOrdinaryNamesAlone()
    {
        Assert.Equal("/my-files/report.pdf", ProtonDriveService.CombinePath("/my-files", "report.pdf"));
        Assert.Equal("/report.pdf", ProtonDriveService.CombinePath("/", "report.pdf"));
        Assert.Equal("/my-files/a/b.txt", ProtonDriveService.CombinePath("/my-files/a", "b.txt"));
    }

    [Fact]
    public async Task Trash_OnANodeWhoseNameContainsASlash_SendsTheEscapedPath()
    {
        var (service, executor) = CreateSut();
        executor.EnqueueOutput("");

        await service.TrashItemAsync(ProtonDriveService.CombinePath("/my-files", "in/voice.pdf"));

        var call = Assert.Single(executor.Calls);
        Assert.Equal(["filesystem", "trash", "/my-files/in\\/voice.pdf"], call.Arguments);
    }

    [Theory]
    [InlineData("foo/bar", true)]
    [InlineData("a/b/c", true)]
    [InlineData("ordinary.txt", false)]
    [InlineData("with spaces and \u00e1ccents.txt", false)]
    [InlineData("back\\slash.txt", false)]
    public void UnmappableNames_AreExactlyThoseContainingASlash(string name, bool expected)
    {
        // '/' is the one byte a Linux filename may not contain, so it's the only name that cannot be
        // mirrored locally at all — a backslash is perfectly legal in a filename.
        Assert.Equal(expected, ProtonDriveService.HasUnmappableName(name));
    }
}
