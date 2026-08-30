using System.Net;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>
/// <see cref="OneDriveOperations"/> against a <see cref="FakeHttpMessageHandler"/> — no real Graph
/// account needed. Fixture JSON shapes here follow Microsoft's published Graph docs
/// (docs/PLAN-CLOUD-PROVIDERS.md §4.3/§4.4); pending live-capture confirmation per this session's
/// plan (Appendix A).
/// </summary>
public class OneDriveOperationsTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.OneDriveOperations").FullName;
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly OneDriveOperations _sut;

    public OneDriveOperationsTests()
    {
        var tokenStore = new OneDriveTokenStore(_tempDir);
        tokenStore.Save(new StoredOneDriveToken { AccessToken = "token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var authenticator = new GraphAuthenticator("client-id", tokenStore, new HttpClient(new FakeHttpMessageHandler()));
        var http = new GraphHttpClient(authenticator, new HttpClient(_handler));
        _sut = new OneDriveOperations(http);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ListFolderAsync_FollowsNextLinkToExhaustion()
    {
        _handler.When(request => request.Method == HttpMethod.Get, request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("page2", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"value":[{"id":"2","name":"b.txt","file":{}}]}
                    """);
            }

            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"value":[{"id":"1","name":"a.txt","file":{}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/page2"}
                """);
        });

        var items = await _sut.ListFolderAsync("/Documents");

        Assert.Equal(["a.txt", "b.txt"], items.Select(item => item.Name));
        // Exactly two GETs: the first page and the one @odata.nextLink pointed at — a partial
        // listing would read as a remote deletion to the sync reconciler.
        Assert.Equal(2, _handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task ListFolderAsync_MapsFolderAndFileFacetsCorrectly()
    {
        _handler.When(HttpMethod.Get, "/children", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[
                {"id":"1","name":"Photos","folder":{"childCount":3}},
                {"id":"2","name":"notes.txt","size":42,"file":{"hashes":{"quickXorHash":"abc123=="}},"fileSystemInfo":{"lastModifiedDateTime":"2026-01-02T03:04:05Z"},"createdBy":{"user":{"email":"me@example.com"}}}
            ]}
            """));

        var items = await _sut.ListFolderAsync("/");
        var folder = items.Single(item => item.Name == "Photos");
        var file = items.Single(item => item.Name == "notes.txt");

        Assert.True(folder.IsFolder);
        Assert.Null(folder.Size);
        Assert.False(file.IsFolder);
        Assert.Equal(42, file.Size);
        Assert.Equal("abc123==", file.ContentHash);
        Assert.Equal("me@example.com", file.Owner);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), file.ModifiedAt);
        Assert.Equal("/notes.txt", file.Path);
    }

    /// <summary>A file with only sha1Hash (no quickXorHash) must not get its hash mislabeled as QuickXor — see OneDriveOperations.ToDriveItem's reasoning.</summary>
    [Fact]
    public async Task ListFolderAsync_AFileWithOnlySha1Hash_GetsNoContentHash()
    {
        _handler.When(HttpMethod.Get, "/children", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[{"id":"1","name":"legacy.txt","size":10,"file":{"hashes":{"sha1Hash":"deadbeef"}}}]}
            """));

        var items = await _sut.ListFolderAsync("/");

        Assert.Null(items.Single().ContentHash);
    }

    [Fact]
    public async Task UploadFilesAsync_ASmallFile_PutsDirectlyToContent()
    {
        var localFile = Path.Combine(_tempDir, "small.txt");
        File.WriteAllText(localFile, "small file content");
        _handler.When(HttpMethod.Put, "/content", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1","name":"small.txt"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None);

        var put = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains("small.txt", put.Url);
        Assert.DoesNotContain("createUploadSession", _handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task UploadFilesAsync_ALargeFile_UsesAChunkedSession()
    {
        var localFile = Path.Combine(_tempDir, "large.bin");
        // Bigger than the 4 MiB single-shot ceiling.
        File.WriteAllBytes(localFile, new byte[5 * 1024 * 1024]);

        _handler.When(HttpMethod.Post, "createUploadSession", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"uploadUrl":"https://upload.example.com/session123"}"""));
        _handler.When(HttpMethod.Put, "upload.example.com", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None);

        Assert.Contains(_handler.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("createUploadSession"));
        var chunkPuts = _handler.Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("upload.example.com")).ToList();
        Assert.NotEmpty(chunkPuts);
        // The upload session URL is pre-authenticated and must NOT carry the bearer header.
        Assert.All(chunkPuts, r => Assert.Null(r.AuthorizationHeader));
    }

    [Fact]
    public async Task UploadFilesAsync_SkipStrategy_TranslatesA409IntoSuccess()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        _handler.When(HttpMethod.Put, "/content", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists"}}"""));

        // Must not throw: Proton's own "skip" conflict behavior silently succeeds, so OneDrive's
        // Skip strategy has to look the same to the caller.
        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.Skip);
    }

    [Fact]
    public async Task UploadFilesAsync_NoneStrategy_ADuplicateNameThrows()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        _handler.When(HttpMethod.Put, "/content", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists"}}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() => _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None));
        Assert.Equal(DriveErrorKind.AlreadyExists, ex.Kind);
    }

    [Theory]
    [InlineData(UploadConflictStrategy.Replace, "replace")]
    [InlineData(UploadConflictStrategy.KeepBoth, "rename")]
    [InlineData(UploadConflictStrategy.None, "fail")]
    [InlineData(UploadConflictStrategy.Skip, "fail")]
    public async Task UploadFilesAsync_MapsConflictStrategyToTheDocumentedBehavior(UploadConflictStrategy strategy, string expectedBehavior)
    {
        var localFile = Path.Combine(_tempDir, "file.txt");
        File.WriteAllText(localFile, "content");
        _handler.When(HttpMethod.Put, "/content", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1"}"""));

        await _sut.UploadFilesAsync([localFile], "/", strategy);

        var put = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains($"conflictBehavior={expectedBehavior}", put.Url);
    }

    [Fact]
    public async Task ListFolderAsync_EncodesEachPathSegmentIndividually()
    {
        _handler.When(HttpMethod.Get, "/children", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}"""));

        await _sut.ListFolderAsync("/My Docs/Q1 report");

        var get = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Get);
        Assert.Contains("My%20Docs/Q1%20report", get.Url);
    }

    [Fact]
    public async Task TrashItemAsync_SendsADelete()
    {
        _handler.When(HttpMethod.Delete, "root:", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await _sut.TrashItemAsync("/notes.txt");

        Assert.Contains(_handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RenameItemAsync_SendsAPatchWithTheNewName()
    {
        _handler.When(HttpMethod.Patch, "root:", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1"}"""));

        await _sut.RenameItemAsync("/old.txt", "new.txt");

        var patch = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("new.txt", patch.Body);
    }
}
