using System.Net;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>
/// <see cref="OneDriveOperations"/>'s <see cref="IDeltaSource"/> implementation — the P8 whole-drive
/// delta query, against a <see cref="FakeHttpMessageHandler"/>. The <c>parentReference.path</c>
/// parser is the riskiest new piece here (delta items arrive with no ambient parent context, unlike
/// <c>ListFolderAsync</c>'s recursive walk), so it gets the most direct coverage.
/// See docs/PLAN-CLOUD-PROVIDERS.md P8.
/// </summary>
public class OneDriveDeltaTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.OneDriveDelta").FullName;
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly IDeltaSource _sut;

    public OneDriveDeltaTests()
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
    public async Task GetChangesAsync_ARootLevelItem_ResolvesToASlashPrefixedPath()
    {
        _handler.When(HttpMethod.Get, "/root/delta", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[{"id":"1","name":"notes.txt","size":10,"parentReference":{"id":"root","path":"/drive/root:"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta-cursor-1"}
            """));

        var result = await _sut.GetChangesAsync(null);

        var change = Assert.Single(result.Changes);
        Assert.Equal("/notes.txt", change.Item.Path);
        Assert.False(change.IsDeleted);
        Assert.Equal("delta-cursor-1", new Uri(result.NextToken!).Segments[^1]);
    }

    [Fact]
    public async Task GetChangesAsync_ANestedUrlEncodedItem_DecodesEachSegment()
    {
        _handler.When(HttpMethod.Get, "/root/delta", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[{"id":"2","name":"report.docx","size":10,"parentReference":{"id":"p","path":"/drive/root:/My%20Docs/Q1%20%C3%B1andu"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/cursor"}
            """));

        var result = await _sut.GetChangesAsync(null);

        Assert.Equal("/My Docs/Q1 ñandu/report.docx", Assert.Single(result.Changes).Item.Path);
    }

    [Fact]
    public async Task GetChangesAsync_ADeletedItem_IsReportedAsDeletedWithAResolvedPath()
    {
        _handler.When(HttpMethod.Get, "/root/delta", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[{"id":"3","name":"gone.txt","parentReference":{"id":"root","path":"/drive/root:"},"deleted":{"state":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/cursor"}
            """));

        var result = await _sut.GetChangesAsync(null);

        var change = Assert.Single(result.Changes);
        Assert.True(change.IsDeleted);
        Assert.Equal("/gone.txt", change.Item.Path);
    }

    [Fact]
    public async Task GetChangesAsync_FollowsNextLinkToExhaustionBeforeReturningTheDeltaLink()
    {
        _handler.When(request => request.Method == HttpMethod.Get, request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("page2", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"value":[{"id":"2","name":"b.txt","parentReference":{"id":"root","path":"/drive/root:"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/final-cursor"}
                    """);
            }

            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"value":[{"id":"1","name":"a.txt","parentReference":{"id":"root","path":"/drive/root:"}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/page2"}
                """);
        });

        var result = await _sut.GetChangesAsync(null);

        Assert.Equal(["a.txt", "b.txt"], result.Changes.Select(c => c.Item.Name));
        Assert.Contains("final-cursor", result.NextToken);
        // A null token means "enumerate the entire current tree" — this call IS a full enumeration,
        // same as the token-expired-and-retried case (only an incremental call with a real prior
        // token would report false).
        Assert.True(result.WasFullResync);
    }

    [Fact]
    public async Task GetChangesAsync_APreviousToken_IsUsedAsTheRequestUrlVerbatim()
    {
        _handler.When(HttpMethod.Get, "cursor-from-last-time", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/next-cursor"}
            """));

        await _sut.GetChangesAsync("https://graph.microsoft.com/v1.0/cursor-from-last-time");

        Assert.Contains(_handler.Requests, r => r.Url.Contains("cursor-from-last-time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChangesAsync_AnIncrementalCallWithAValidToken_IsNotFlaggedAsAFullResync()
    {
        _handler.When(HttpMethod.Get, "cursor-from-last-time", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/next-cursor"}
            """));

        var result = await _sut.GetChangesAsync("https://graph.microsoft.com/v1.0/cursor-from-last-time");

        Assert.False(result.WasFullResync);
    }

    [Fact]
    public async Task GetChangesAsync_A410GoneOnAStoredToken_RetriesOnceFromScratchAndFlagsAFullResync()
    {
        var callCount = 0;
        _handler.When(HttpMethod.Get, "/root/delta", _ =>
        {
            callCount++;
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"value":[{"id":"1","name":"a.txt","parentReference":{"id":"root","path":"/drive/root:"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/fresh-cursor"}
                """);
        });
        _handler.When(HttpMethod.Get, "expired-token", _ => FakeHttpMessageHandler.Json(HttpStatusCode.Gone, """{"error":{"code":"resyncRequired"}}"""));

        var result = await _sut.GetChangesAsync("https://graph.microsoft.com/v1.0/expired-token");

        Assert.True(result.WasFullResync);
        Assert.Equal("a.txt", Assert.Single(result.Changes).Item.Name);
    }

    /// <summary>
    /// Safety net for a pagination bug that would otherwise page forever without ever reaching a
    /// terminal @odata.deltaLink — found live: a real account hung the sync scheduler for minutes
    /// on a drive with only a few hundred items (docs/PLAN-CLOUD-PROVIDERS.md P8's own "pending
    /// live verification" note). Must fail loudly well before that, not hang.
    /// </summary>
    [Fact]
    public async Task GetChangesAsync_APageThatNeverReachesADeltaLink_AbortsInsteadOfLoopingForever()
    {
        var callCount = 0;
        _handler.When(HttpMethod.Get, "/root/delta", _ =>
        {
            callCount++;
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, $$"""
                {"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/root/delta?page={{callCount}}"}
                """);
        });

        var ex = await Assert.ThrowsAsync<DriveException>(() => _sut.GetChangesAsync(null));

        Assert.Contains("no terminó", ex.Message, StringComparison.Ordinal);
    }
}
