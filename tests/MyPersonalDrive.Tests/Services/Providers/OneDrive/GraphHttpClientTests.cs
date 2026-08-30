using System.Net;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>
/// <see cref="GraphHttpClient"/>'s retry policy: 401 forces exactly one token refresh and one
/// retry, 429/503 honors <c>Retry-After</c> and retries exactly once, and a non-success response's
/// body is classified into the right <see cref="DriveErrorKind"/> before being thrown.
/// docs/PLAN-CLOUD-PROVIDERS.md §4.2/§4.5.
/// </summary>
public class GraphHttpClientTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.GraphHttpClient").FullName;

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

    private (GraphHttpClient Client, FakeHttpMessageHandler ResourceHandler, FakeHttpMessageHandler AuthHandler) Build(DateTimeOffset tokenExpiresAt)
    {
        var tokenStore = new OneDriveTokenStore(_tempDir);
        tokenStore.Save(new StoredOneDriveToken { AccessToken = "initial-token", RefreshToken = "refresh-token", ExpiresAt = tokenExpiresAt });

        var authHandler = new FakeHttpMessageHandler();
        var authenticator = new GraphAuthenticator("test-client-id", tokenStore, new HttpClient(authHandler));

        var resourceHandler = new FakeHttpMessageHandler();
        var client = new GraphHttpClient(authenticator, new HttpClient(resourceHandler));
        return (client, resourceHandler, authHandler);
    }

    [Fact]
    public async Task SendAsync_On401_RefreshesOnceAndRetriesOnce()
    {
        var (client, resourceHandler, authHandler) = Build(DateTimeOffset.UtcNow.AddHours(1));

        var attempt = 0;
        resourceHandler.When(HttpMethod.Get, "resource", _ =>
        {
            attempt++;
            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"code":"InvalidAuthenticationToken"}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        authHandler.When(HttpMethod.Post, "token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"refreshed-token","refresh_token":"refresh-token","expires_in":3600,"token_type":"Bearer"}"""));

        using var response = await client.SendAsync("GET resource", () => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/resource"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempt);
        // Exactly one refresh, not one per retry attempt — the authenticator also makes an
        // incidental GET /me for the account label, which is fine to happen once too.
        Assert.Single(authHandler.Requests, request => request.Url.Contains("/token", StringComparison.Ordinal));
        Assert.Equal("Bearer refreshed-token", resourceHandler.Requests[1].AuthorizationHeader);
    }

    [Fact]
    public async Task SendAsync_TwoConsecutive401s_ThrowsRatherThanLoopingForever()
    {
        var (client, resourceHandler, authHandler) = Build(DateTimeOffset.UtcNow.AddHours(1));

        resourceHandler.When(HttpMethod.Get, "resource", _ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"code":"InvalidAuthenticationToken"}}""") });
        authHandler.When(HttpMethod.Post, "token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"still-bad","refresh_token":"refresh-token","expires_in":3600,"token_type":"Bearer"}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() =>
            client.SendAsync("GET resource", () => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/resource")));

        Assert.Equal(DriveErrorKind.NotAuthenticated, ex.Kind);
    }

    [Fact]
    public async Task SendAsync_On429_HonorsRetryAfterAndRetriesOnce()
    {
        var (client, resourceHandler, _) = Build(DateTimeOffset.UtcNow.AddHours(1));

        var attempt = 0;
        resourceHandler.When(HttpMethod.Get, "resource", _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var throttled = new HttpResponseMessage((HttpStatusCode)429);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return throttled;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        using var response = await client.SendAsync("GET resource", () => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/resource"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task SendAsync_ANonSuccessResponse_ThrowsWithTheClassifiedKind()
    {
        var (client, resourceHandler, _) = Build(DateTimeOffset.UtcNow.AddHours(1));

        resourceHandler.When(HttpMethod.Get, "resource", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, """{"error":{"code":"itemNotFound","message":"The resource could not be found."}}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() =>
            client.SendAsync("GET resource", () => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/resource")));

        Assert.Equal(DriveErrorKind.NotFound, ex.Kind);
        Assert.Contains("could not be found", ex.Message);
    }
}
