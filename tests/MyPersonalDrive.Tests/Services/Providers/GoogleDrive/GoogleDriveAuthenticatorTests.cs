using System.Net;
using System.Security.Cryptography;
using System.Text;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.GoogleDrive;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.GoogleDrive;

/// <summary>
/// The pieces of <see cref="GoogleDriveAuthenticator"/> testable without a real browser: PKCE
/// verifier/challenge generation (exposed <c>internal</c>, reachable here via the app project's
/// <c>InternalsVisibleTo</c> for the test assembly) and the token-refresh/force-refresh path
/// against a fake token endpoint, including the Google-specific <c>client_secret</c> body field
/// and the keep-old-refresh-token-when-absent rule.
/// </summary>
public class GoogleDriveAuthenticatorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.GoogleDriveAuthenticator").FullName;

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
    public void GeneratePkceVerifier_IsWithinThePkceLengthRequirement()
    {
        var verifier = GoogleDriveAuthenticator.GeneratePkceVerifier();

        Assert.InRange(verifier.Length, 43, 128);
        // base64url: no padding, no '+'/'/' — RFC 7636's code-verifier charset.
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
    }

    [Fact]
    public void GeneratePkceVerifier_ProducesADifferentValueEachTime()
    {
        Assert.NotEqual(GoogleDriveAuthenticator.GeneratePkceVerifier(), GoogleDriveAuthenticator.GeneratePkceVerifier());
    }

    [Fact]
    public void ComputePkceChallenge_IsTheBase64UrlSha256OfTheVerifier()
    {
        const string verifier = "a-fixed-test-verifier-string-for-this-assertion-1234567890";

        var challenge = GoogleDriveAuthenticator.ComputePkceChallenge(verifier);

        var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expected = Convert.ToBase64String(expectedHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    private (GoogleDriveAuthenticator Authenticator, GoogleDriveTokenStore TokenStore, FakeHttpMessageHandler AuthHandler) Build(DateTimeOffset tokenExpiresAt, string? refreshToken = "refresh-token")
    {
        var tokenStore = new GoogleDriveTokenStore(_tempDir);
        tokenStore.Save(new StoredGoogleDriveToken { AccessToken = "initial-token", RefreshToken = refreshToken ?? string.Empty, ExpiresAt = tokenExpiresAt });

        var authHandler = new FakeHttpMessageHandler();
        var authenticator = new GoogleDriveAuthenticator("client-id", "client-secret", tokenStore, new HttpClient(authHandler));
        return (authenticator, tokenStore, authHandler);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WithATokenFarFromExpiry_ReturnsItWithoutRefreshing()
    {
        var (authenticator, _, authHandler) = Build(DateTimeOffset.UtcNow.AddHours(1));

        var token = await authenticator.GetValidAccessTokenAsync();

        Assert.Equal("initial-token", token);
        Assert.Empty(authHandler.Requests);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WithATokenNearExpiry_RefreshesAndSendsTheClientSecret()
    {
        var (authenticator, _, authHandler) = Build(DateTimeOffset.UtcNow.AddMinutes(1));
        authHandler.When(HttpMethod.Post, "oauth2.googleapis.com/token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"refreshed-token","refresh_token":"new-refresh-token","expires_in":3600,"token_type":"Bearer"}"""));

        var token = await authenticator.GetValidAccessTokenAsync();

        Assert.Equal("refreshed-token", token);
        // The stored token has no cached account label, so the refresh also triggers one
        // best-effort GET to `about` for it — the refresh call itself is the one being asserted on.
        var refreshRequest = Assert.Single(authHandler.Requests, r => r.Url.Contains("/token", StringComparison.Ordinal));
        Assert.Contains("client_secret=client-secret", refreshRequest.Body);
        Assert.Contains("grant_type=refresh_token", refreshRequest.Body);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheResponseOmitsARefreshToken_KeepsTheOldOne()
    {
        var (authenticator, tokenStore, authHandler) = Build(DateTimeOffset.UtcNow.AddMinutes(1));
        authHandler.When(HttpMethod.Post, "token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"refreshed-token","expires_in":3600,"token_type":"Bearer"}"""));

        await authenticator.GetValidAccessTokenAsync();

        Assert.Equal("refresh-token", tokenStore.Load()!.RefreshToken);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WithNoStoredSession_ThrowsNotAuthenticated()
    {
        var tokenStore = new GoogleDriveTokenStore(_tempDir);
        var authenticator = new GoogleDriveAuthenticator("client-id", "client-secret", tokenStore, new HttpClient(new FakeHttpMessageHandler()));

        var ex = await Assert.ThrowsAsync<DriveException>(() => authenticator.GetValidAccessTokenAsync());

        Assert.Equal(DriveErrorKind.NotAuthenticated, ex.Kind);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WithNoRefreshToken_ThrowsRatherThanCallingTheNetwork()
    {
        var (authenticator, _, authHandler) = Build(DateTimeOffset.UtcNow.AddMinutes(1), refreshToken: string.Empty);

        var ex = await Assert.ThrowsAsync<DriveException>(() => authenticator.GetValidAccessTokenAsync());

        Assert.Equal(DriveErrorKind.NotAuthenticated, ex.Kind);
        Assert.Empty(authHandler.Requests);
    }

    [Fact]
    public async Task ForceRefreshAsync_RefreshesRegardlessOfCachedExpiry()
    {
        var (authenticator, _, authHandler) = Build(DateTimeOffset.UtcNow.AddHours(1));
        authHandler.When(HttpMethod.Post, "token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"forced-refresh-token","refresh_token":"refresh-token","expires_in":3600,"token_type":"Bearer"}"""));

        var token = await authenticator.ForceRefreshAsync();

        Assert.Equal("forced-refresh-token", token);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheTokenEndpointReturnsAnError_ThrowsNotAuthenticatedWithTheDescription()
    {
        var (authenticator, _, authHandler) = Build(DateTimeOffset.UtcNow.AddMinutes(1));
        authHandler.When(HttpMethod.Post, "token", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        var ex = await Assert.ThrowsAsync<DriveException>(() => authenticator.GetValidAccessTokenAsync());

        Assert.Equal(DriveErrorKind.NotAuthenticated, ex.Kind);
        Assert.Contains("expired or revoked", ex.Message);
    }
}
