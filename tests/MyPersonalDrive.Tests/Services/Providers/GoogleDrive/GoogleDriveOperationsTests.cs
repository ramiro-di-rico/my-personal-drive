using System.Net;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.GoogleDrive;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.GoogleDrive;

/// <summary>
/// <see cref="GoogleDriveOperations"/> against a <see cref="FakeHttpMessageHandler"/> — no real
/// Drive account needed. Fixture JSON shapes follow Google's published Drive API v3 docs
/// (docs/PLAN-CLOUD-PROVIDERS.md §8), pending live-capture confirmation.
/// </summary>
public class GoogleDriveOperationsTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.GoogleDriveOperations").FullName;
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly GoogleDriveOperations _sut;

    public GoogleDriveOperationsTests()
    {
        var tokenStore = new GoogleDriveTokenStore(_tempDir);
        tokenStore.Save(new StoredGoogleDriveToken { AccessToken = "token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var authenticator = new GoogleDriveAuthenticator("client-id", "client-secret", tokenStore, new HttpClient(new FakeHttpMessageHandler()));
        var http = new GoogleDriveHttpClient(authenticator, new HttpClient(_handler));
        _sut = new GoogleDriveOperations(http);
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

    /// <summary>Every request's decoded query string — routing predicates match against this rather than the raw percent-encoded URL, since Drive's `q` filter is itself URL-escaped inside the outer query string.</summary>
    private static bool DecodedUrlContains(HttpRequestMessage request, string expected)
        => DecodedUrlContains(request.RequestUri!.ToString(), expected);

    private static bool DecodedUrlContains(string url, string expected)
        => Uri.UnescapeDataString(url).Contains(expected, StringComparison.Ordinal);

    /// <summary>Registers a GET route matched against the decoded URL — for routes whose match text contains a `'` or other character Drive's own `q` filter gets percent-encoded (see <see cref="DecodedUrlContains(HttpRequestMessage, string)"/>).</summary>
    private void WhenGet(string decodedUrlContains, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _handler.When(request => request.Method == HttpMethod.Get && DecodedUrlContains(request, decodedUrlContains), respond);

    [Fact]
    public async Task ListFolderAsync_FollowsNextPageTokenToExhaustion()
    {
        _handler.When(request => request.Method == HttpMethod.Get && DecodedUrlContains(request, "'root' in parents"), request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("pageToken", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"files":[{"id":"2","name":"b.txt","mimeType":"text/plain"}]}
                    """);
            }

            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"files":[{"id":"1","name":"a.txt","mimeType":"text/plain"}],"nextPageToken":"page2"}
                """);
        });

        var items = await _sut.ListFolderAsync("/");

        Assert.Equal(["a.txt", "b.txt"], items.Select(item => item.Name));
        Assert.Equal(2, _handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task ListFolderAsync_MapsFolderAndFileCorrectly()
    {
        WhenGet("'root' in parents", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"files":[
                {"id":"1","name":"Photos","mimeType":"application/vnd.google-apps.folder"},
                {"id":"2","name":"notes.txt","mimeType":"text/plain","size":42,"modifiedTime":"2026-01-02T03:04:05Z","sha256Checksum":"abc123"}
            ]}
            """));

        var items = await _sut.ListFolderAsync("/");
        var folder = items.Single(item => item.Name == "Photos");
        var file = items.Single(item => item.Name == "notes.txt");

        Assert.True(folder.IsFolder);
        Assert.Null(folder.Size);
        Assert.False(file.IsFolder);
        Assert.Equal(42, file.Size);
        Assert.Equal("abc123", file.ContentHash);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), file.ModifiedAt);
        Assert.Equal("/notes.txt", file.Path);
        Assert.False(file.IsRemoteOnlyDocument);
    }

    /// <summary>A file with only md5Checksum (no sha256Checksum) must not get its hash mislabeled as Sha256 — see GoogleDriveOperations.ToDriveItem's reasoning.</summary>
    [Fact]
    public async Task ListFolderAsync_AFileWithOnlyMd5Checksum_GetsNoContentHash()
    {
        WhenGet("'root' in parents", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"files":[{"id":"1","name":"legacy.txt","mimeType":"text/plain","size":10,"md5Checksum":"deadbeef"}]}
            """));

        var items = await _sut.ListFolderAsync("/");

        Assert.Null(items.Single().ContentHash);
    }

    [Fact]
    public async Task ListFolderAsync_AGoogleNativeFile_IsMarkedAsARemoteOnlyDocument_WithNoHashOrDownloadableSize()
    {
        WhenGet("'root' in parents", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"files":[{"id":"1","name":"Quarterly Plan","mimeType":"application/vnd.google-apps.document"}]}
            """));

        var items = await _sut.ListFolderAsync("/");
        var doc = Assert.Single(items);

        Assert.True(doc.IsRemoteOnlyDocument);
        Assert.False(doc.IsFolder);
        Assert.Null(doc.ContentHash);
    }

    [Fact]
    public async Task ResolveIdAsync_WithDuplicateNamedSiblings_TheFirstMatchInListingOrderWins()
    {
        // Two folders both literally named "Docs" under root — Drive allows this
        // (docs/PLAN-CLOUD-PROVIDERS.md §8.2). Resolving "/Docs/report.pdf" must deterministically
        // pick the first one returned, never merge or error.
        WhenGet("name='Docs'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"files":[{"id":"docs-first","name":"Docs"},{"id":"docs-second","name":"Docs"}]}
            """));
        _handler.When(request => request.Method == HttpMethod.Get && DecodedUrlContains(request, "'docs-first' in parents") && DecodedUrlContains(request, "name='report.pdf'"),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"report-id","name":"report.pdf"}]}"""));
        _handler.When(HttpMethod.Get, "alt=media", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("file content") });

        await _sut.DownloadFileAsync("/Docs/report.pdf", _tempDir);

        // Only the first "Docs" folder's children were ever queried — proves "docs-first" was
        // picked, not "docs-second" (which has no route registered and would 404 if queried).
        Assert.Contains(_handler.Requests, r => DecodedUrlContains(r.Url, "'docs-first' in parents"));
    }

    [Fact]
    public async Task UploadFilesAsync_ASmallFile_UsesMultipartUpload()
    {
        var localFile = Path.Combine(_tempDir, "small.txt");
        File.WriteAllText(localFile, "small file content");
        WhenGet("name='small.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));
        _handler.When(HttpMethod.Post, "uploadType=multipart", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1","name":"small.txt"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None);

        var post = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("uploadType=multipart", post.Url);
        Assert.DoesNotContain("uploadType=resumable", _handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task UploadFilesAsync_ALargeFile_UsesAResumableSession()
    {
        var localFile = Path.Combine(_tempDir, "large.bin");
        // Bigger than the 5 MiB single-shot ceiling.
        File.WriteAllBytes(localFile, new byte[6 * 1024 * 1024]);
        WhenGet("name='large.bin'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));
        _handler.When(request => request.Method == HttpMethod.Post && request.RequestUri!.ToString().Contains("uploadType=resumable", StringComparison.Ordinal), _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://upload.example.com/session123");
            return response;
        });
        _handler.When(HttpMethod.Put, "upload.example.com", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"1","name":"large.bin"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None);

        Assert.Contains(_handler.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("uploadType=resumable"));
        var chunkPuts = _handler.Requests.Where(r => r.Method == HttpMethod.Put && r.Url.Contains("upload.example.com")).ToList();
        Assert.NotEmpty(chunkPuts);
        // The resumable session URI is pre-authenticated and must NOT carry the bearer header.
        Assert.All(chunkPuts, r => Assert.Null(r.AuthorizationHeader));
    }

    [Fact]
    public async Task UploadFilesAsync_NoneStrategy_ADuplicateNameThrowsAlreadyExists()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        WhenGet("name='existing.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"existing-id","name":"existing.txt"}]}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() => _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.None));

        Assert.Equal(DriveErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task UploadFilesAsync_SkipStrategy_ADuplicateNameSucceedsWithNoUpload()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        WhenGet("name='existing.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"existing-id","name":"existing.txt"}]}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.Skip);

        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task UploadFilesAsync_ReplaceStrategy_PatchesTheExistingFileById()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        WhenGet("name='existing.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"existing-id","name":"existing.txt"}]}"""));
        _handler.When(HttpMethod.Patch, "files/existing-id", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"existing-id","name":"existing.txt"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.Replace);

        Assert.Contains(_handler.Requests, r => r.Method == HttpMethod.Patch && r.Url.Contains("files/existing-id"));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UploadFilesAsync_KeepBothStrategy_CreatesUnderANumberedSuffix()
    {
        var localFile = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(localFile, "content");
        WhenGet("name='existing.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"existing-id","name":"existing.txt"}]}"""));
        WhenGet("name='existing (2).txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));
        _handler.When(HttpMethod.Post, "uploadType=multipart", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"new-id","name":"existing (2).txt"}"""));

        await _sut.UploadFilesAsync([localFile], "/", UploadConflictStrategy.KeepBoth);

        var post = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("existing (2).txt", post.Body);
    }

    [Fact]
    public async Task TrashItemAsync_PatchesTrashedTrue()
    {
        WhenGet("name='notes.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"note-id","name":"notes.txt"}]}"""));
        _handler.When(HttpMethod.Patch, "files/note-id", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"note-id"}"""));

        await _sut.TrashItemAsync("/notes.txt");

        var patch = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("\"trashed\"", patch.Body);
        Assert.Contains("true", patch.Body);
    }

    [Fact]
    public async Task RenameItemAsync_SendsAPatchWithTheNewName()
    {
        WhenGet("name='old.txt'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"old-id","name":"old.txt"}]}"""));
        _handler.When(HttpMethod.Patch, "files/old-id", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"old-id"}"""));

        await _sut.RenameItemAsync("/old.txt", "new.txt");

        var patch = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("new.txt", patch.Body);
    }

    [Fact]
    public async Task CreateFolderAsync_PostsWithTheFolderMimeType()
    {
        _handler.When(HttpMethod.Post, "files", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"new-folder-id","name":"Reports"}"""));

        await _sut.CreateFolderAsync("/", "Reports");

        var post = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("application/vnd.google-apps.folder", post.Body);
        Assert.Contains("Reports", post.Body);
    }

    [Fact]
    public async Task MoveItemsAsync_ResolvesTheCurrentParentThenPatchesAddAndRemoveParents()
    {
        WhenGet("name='Archive'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"archive-id","name":"Archive"}]}"""));
        WhenGet("name='report.pdf'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"report-id","name":"report.pdf"}]}"""));
        _handler.When(request => request.Method == HttpMethod.Get && request.RequestUri!.ToString().Contains("fields=parents", StringComparison.Ordinal),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"report-id","parents":["root"]}"""));
        _handler.When(HttpMethod.Patch, "files/report-id", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"report-id"}"""));

        await _sut.MoveItemsAsync(["/report.pdf"], "/Archive");

        var patch = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("addParents=archive-id", patch.Url);
        Assert.Contains("removeParents=root", patch.Url);
    }

    [Fact]
    public async Task CopyItemAsync_IsASingleSynchronousPostWithNoPolling()
    {
        WhenGet("name='Archive'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"archive-id","name":"Archive"}]}"""));
        WhenGet("name='report.pdf'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"report-id","name":"report.pdf"}]}"""));
        _handler.When(HttpMethod.Post, "files/report-id/copy", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"copy-id","name":"report.pdf"}"""));

        await _sut.CopyItemAsync("/report.pdf", "/Archive");

        // Exactly one POST for the copy itself — no monitor-URL polling the way Graph's async copy needs.
        Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("/copy"));
    }

    [Fact]
    public async Task CreateShareLinkAsync_CreatesAnAnyoneReaderPermission_AndReturnsTheWebViewLink()
    {
        WhenGet("name='report.pdf'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"report-id","name":"report.pdf"}]}"""));
        _handler.When(HttpMethod.Post, "permissions", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"perm1"}"""));
        _handler.When(request => request.Method == HttpMethod.Get && request.RequestUri!.ToString().Contains("fields=webViewLink", StringComparison.Ordinal),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"report-id","webViewLink":"https://drive.google.com/file/d/report-id/view"}"""));

        var url = await _sut.CreateShareLinkAsync("/report.pdf");

        Assert.Equal("https://drive.google.com/file/d/report-id/view", url);
        var permissionPost = Assert.Single(_handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("\"reader\"", permissionPost.Body);
        Assert.Contains("\"anyone\"", permissionPost.Body);
    }

    [Fact]
    public async Task CreateShareLinkAsync_WithNoLinkInTheResponse_ThrowsRatherThanReturningAnEmptyUrl()
    {
        WhenGet("name='report.pdf'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[{"id":"report-id","name":"report.pdf"}]}"""));
        _handler.When(HttpMethod.Post, "permissions", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"perm1"}"""));
        _handler.When(request => request.Method == HttpMethod.Get && request.RequestUri!.ToString().Contains("fields=webViewLink", StringComparison.Ordinal),
            _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"report-id"}"""));

        await Assert.ThrowsAsync<DriveException>(() => _sut.CreateShareLinkAsync("/report.pdf"));
    }

    [Fact]
    public async Task ResolveIdAsync_WhenASegmentIsNotFound_ThrowsNotFound()
    {
        WhenGet("name='Missing'", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"files":[]}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() => _sut.DownloadFileAsync("/Missing/file.txt", _tempDir));

        Assert.Equal(DriveErrorKind.NotFound, ex.Kind);
    }
}
