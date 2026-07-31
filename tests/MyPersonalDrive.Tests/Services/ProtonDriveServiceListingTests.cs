using MyPersonalDrive.Services;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B2.1: an empty-but-valid JSON listing must render as an empty
/// folder, never fall through to the text heuristic parser, and an unrecognized JSON shape must
/// be a hard error rather than a silently empty listing.
/// </summary>
public class ProtonDriveServiceListingTests
{
    [Fact]
    public async Task EmptyJsonArray_IsAnEmptyFolder_NotAParseFailure()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files/empty");

        Assert.Empty(items);
    }

    [Fact]
    public async Task EmptyItemsObject_IsAnEmptyFolder_NotAParseFailure()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""{ "items": [] }""");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files/empty");

        Assert.Empty(items);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("entries")]
    [InlineData("children")]
    public async Task RecognizedContainerKeys_AreAllParsed(string key)
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($$"""{ "{{key}}": [ { "name": "report.pdf", "type": "file", "size": 42 } ] }""");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files/docs");

        var item = Assert.Single(items);
        Assert.Equal("report.pdf", item.Name);
        Assert.False(item.IsFolder);
        Assert.Equal(42, item.Size);
    }

    [Fact]
    public async Task RootArray_WithEntries_IsParsed()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""[ { "name": "Photos", "type": "folder" } ]""");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files");

        var item = Assert.Single(items);
        Assert.Equal("Photos", item.Name);
        Assert.True(item.IsFolder);
        Assert.Equal("/my-files/Photos", item.Path);
    }

    [Fact]
    public async Task UnrecognizedJsonShape_ThrowsInsteadOfReturningEmpty()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""{ "unexpectedField": "value" }""");
        var service = new ProtonDriveService(executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadFolderAsync("/my-files"));
    }

    [Fact]
    public async Task NonJsonOutput_FallsBackToTextParsing_ByDefault()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos\n📄 report.pdf");
        var service = new ProtonDriveService(executor, strictListingParsing: false);

        var items = await service.LoadFolderAsync("/my-files");

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Name == "Photos" && i.IsFolder);
        Assert.Contains(items, i => i.Name == "report.pdf" && !i.IsFolder);
    }

    [Fact]
    public async Task NonJsonOutput_ThrowsWhenStrictParsingIsEnabled()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos\n📄 report.pdf");
        var service = new ProtonDriveService(executor, strictListingParsing: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadFolderAsync("/my-files"));
    }

    [Fact]
    public async Task NonJsonOutput_RaisesListingParseWarning()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos");
        var service = new ProtonDriveService(executor);

        string? warning = null;
        service.ListingParseWarning += (_, message) => warning = message;

        await service.LoadFolderAsync("/my-files");

        Assert.NotNull(warning);
        Assert.Contains("/my-files", warning);
    }

    [Fact]
    public async Task AliasedFieldNames_AreAllRead()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""
            {
              "items": [
                { "title": "notes.txt", "kind": "file", "bytes": 10, "updatedAt": "2026-01-01T00:00:00Z", "user": "ramiro", "shared": true }
              ]
            }
            """);
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("notes.txt", item.Name);
        Assert.Equal(10, item.Size);
        Assert.Equal("ramiro", item.Owner);
        Assert.True(item.IsShared);
    }
}
