using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Sign-in/out for Google Drive: authorization-code + PKCE via a loopback listener, structurally
/// the same shape as <c>OneDrive.GraphAuthenticator</c> (docs/PLAN-CLOUD-PROVIDERS.md §8.1). Real
/// differences from OneDrive's flow, all confirmed against Google's published OAuth2 docs during
/// Phase 1's research pass:
/// <list type="bullet">
/// <item>Redirect uses the loopback IP form <c>http://127.0.0.1:{port}/</c>, not the literal
/// hostname <c>localhost</c> Azure requires.</item>
/// <item><c>access_type=offline</c> and <c>prompt=consent</c> are required on the authorize URL —
/// without the former Google never issues a refresh token at all; without the latter it only
/// issues one on the very first consent ever, silently omitting it on every later re-auth.</item>
/// <item>Google's "Desktop app" OAuth clients are still issued a <c>client_secret</c>, which has to
/// be sent on both the code exchange and the refresh — unlike OneDrive's public client, which needs
/// none. Not a real secret in the confidentiality sense (it ships inside a public, downloadable
/// app), same accepted-risk framing as the plaintext token store itself.</item>
/// </list>
/// </summary>
public sealed class GoogleDriveAuthenticator : IDriveAuthenticator
{
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/drive";
    private const string AboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=user";

    /// <summary>Refresh this far ahead of the stored expiry, so a request in flight never races an about-to-expire token.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long <see cref="AuthenticateAsync"/> waits for the browser to complete the sign-in and
    /// redirect back to the loopback listener, before giving up. Without this, an abandoned or
    /// failed browser flow (no default browser, the user closes the tab, <c>xdg-open</c> not
    /// configured) left <c>HttpListener.GetContextAsync()</c> waiting forever — and since the
    /// caller keeps <c>IsLoading</c> true for the whole duration, every other <c>!IsLoading</c>-gated
    /// command in the app (including switching the browsed provider) went silently unresponsive
    /// along with it. Found live: a real sign-in attempt against this provider hung the whole UI
    /// exactly this way (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A).
    /// </summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly GoogleDriveTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public GoogleDriveAuthenticator(string clientId, string clientSecret, GoogleDriveTokenStore tokenStore, HttpClient? httpClient = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenStore = tokenStore;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Raises the same <see cref="ProviderActivity"/> shape as the other providers, so the console shows the authorize URL and the sign-in outcome.</summary>
    public event EventHandler<ProviderActivity>? Activity;

    public string? AccountLabel => _tokenStore.Load()?.AccountLabel;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new LocalizedInvalidOperationException(
                "No Google Drive client ID is configured.",
                LocalizedText.Of(StringKeys.Error.AuthNoClientId, "Google Drive"));
        }

        var verifier = GeneratePkceVerifier();
        var challenge = ComputePkceChallenge(verifier);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        // Same loopback-reservation dance as GraphAuthenticator, but the redirect Google's docs
        // recommend for an installed app is the loopback IP form, not the literal "localhost".
        var port = ReserveLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var redirectUri = $"http://127.0.0.1:{port}/";

        var authorizeUrl = BuildAuthorizeUrl(redirectUri, challenge, state);
        Activity?.Invoke(this, new ProviderActivity(ActivityKind.Started, $"GET {AuthorizeEndpoint}", Text: authorizeUrl, IsError: false, ExitCode: null, Duration: null));

        TryLaunchBrowser(authorizeUrl);

        // Bounds the wait below to SignInTimeout, distinguishable from the caller's own
        // cancellationToken so a timeout gets its own clear message instead of a bare
        // OperationCanceledException.
        using var timeoutCts = new CancellationTokenSource(SignInTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var code = await WaitForRedirectAsync(listener, state, linkedCts.Token);
            var token = await ExchangeCodeForTokenAsync(code, verifier, redirectUri, cancellationToken);
            var accountLabel = await TryFetchAccountLabelAsync(token.AccessToken, cancellationToken);

            _tokenStore.Save(new StoredGoogleDriveToken
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken ?? string.Empty,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                AccountLabel = accountLabel,
            });

            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, $"GET {AuthorizeEndpoint}", Text: null, IsError: false, ExitCode: 0, Duration: null));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, $"GET {AuthorizeEndpoint}", Text: null, IsError: true, ExitCode: 1, Duration: null));
            throw SignInTimedOut();
        }
        catch
        {
            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, $"GET {AuthorizeEndpoint}", Text: null, IsError: true, ExitCode: 1, Duration: null));
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _tokenStore.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a token good for at least <see cref="RefreshMargin"/>, refreshing first if needed.
    /// Throws a <see cref="DriveException"/> tagged <see cref="DriveErrorKind.NotAuthenticated"/>
    /// when there is no stored token or the refresh itself fails.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var stored = _tokenStore.Load();
        if (stored is null)
        {
            throw NotAuthenticated($"There is no saved Google Drive session.", LocalizedText.Of(StringKeys.Error.AuthNoSession, "Google Drive"));
        }

        if (stored.ExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
        {
            return stored.AccessToken;
        }

        return await RefreshAsync(stored, cancellationToken);
    }

    /// <summary>Forces a refresh regardless of cached expiry — the 401-retry-once path in <see cref="GoogleDriveHttpClient"/>.</summary>
    public async Task<string> ForceRefreshAsync(CancellationToken cancellationToken = default)
    {
        var stored = _tokenStore.Load() ?? throw NotAuthenticated($"There is no saved Google Drive session.", LocalizedText.Of(StringKeys.Error.AuthNoSession, "Google Drive"));
        return await RefreshAsync(stored, cancellationToken);
    }

    private async Task<string> RefreshAsync(StoredGoogleDriveToken stored, CancellationToken cancellationToken)
    {
        // Concurrent requests hitting a 401 at the same time must not each spend the one refresh
        // token — mirrors GraphAuthenticator.RefreshAsync's own reasoning exactly.
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = _tokenStore.Load();
            if (current is not null && !string.Equals(current.AccessToken, stored.AccessToken, StringComparison.Ordinal))
            {
                return current.AccessToken;
            }

            if (string.IsNullOrEmpty(stored.RefreshToken))
            {
                throw NotAuthenticated("There is no saved refresh token.", LocalizedText.Of(StringKeys.Error.AuthNoRefreshToken));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = stored.RefreshToken,
                }),
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = ParseTokenResponse(body, response.IsSuccessStatusCode);

            var accountLabel = stored.AccountLabel ?? await TryFetchAccountLabelAsync(token.AccessToken, cancellationToken);
            var refreshed = new StoredGoogleDriveToken
            {
                AccessToken = token.AccessToken,
                // Google's refresh response does not always include a new refresh token; keep the
                // old one when it doesn't, same OAuth2 refresh-token-rotation convention GraphAuthenticator follows.
                RefreshToken = token.RefreshToken ?? stored.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                AccountLabel = accountLabel,
            };
            _tokenStore.Save(refreshed);
            return refreshed.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            throw NotAuthenticated($"Could not renew the Google Drive session: {ex.Message}", LocalizedText.Of(StringKeys.Error.AuthRefreshFailed, "Google Drive", ex.Message));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<GoogleDriveTokenResponse> ExchangeCodeForTokenAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTokenResponse(body, response.IsSuccessStatusCode);
    }

    private static GoogleDriveTokenResponse ParseTokenResponse(string body, bool wasSuccessStatus)
    {
        GoogleDriveTokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize(body, AppJsonContext.Default.GoogleDriveTokenResponse);
        }
        catch (JsonException)
        {
            throw NotAuthenticated("The token endpoint returned an unreadable response.", LocalizedText.Of(StringKeys.Error.AuthTokenUnparsable));
        }

        if (token is null || !wasSuccessStatus || string.IsNullOrEmpty(token.AccessToken))
        {
            var reason = token?.ErrorDescription ?? token?.Error ?? "unknown error";
            throw NotAuthenticated($"Google Drive sign-in failed: {reason}", LocalizedText.Of(StringKeys.Error.AuthSignInFailed, "Google Drive", reason));
        }

        return token;
    }

    private async Task<string?> TryFetchAccountLabelAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AboutEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var about = JsonSerializer.Deserialize(body, AppJsonContext.Default.GoogleDriveAboutResponse);
            return about?.User?.EmailAddress ?? about?.User?.DisplayName;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The account label is cosmetic (shown in the settings card); a failure to fetch it
            // must not fail sign-in itself.
            return null;
        }
    }

    private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = _clientId;
        query["response_type"] = "code";
        query["redirect_uri"] = redirectUri;
        query["scope"] = Scope;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        // Google-specific, and easy to miss: without access_type=offline no refresh_token is ever
        // issued; without prompt=consent one is issued only on the very first consent ever, and
        // silently omitted on every later re-auth — which would silently break token persistence
        // after a logout/re-login cycle (docs/PLAN-CLOUD-PROVIDERS.md §8.1).
        query["access_type"] = "offline";
        query["prompt"] = "consent";
        return $"{AuthorizeEndpoint}?{query}";
    }

    private static void TryLaunchBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser to launch (or launching it failed) — the URL is still on the
            // console via the Activity event above, so sign-in can proceed manually.
        }
    }

    private static async Task<string> WaitForRedirectAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(listener.Stop);
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var error = query["error"];
        var code = query["code"];
        var state = query["state"];

        var responseHtml = error is null
            ? "<html><body>Signed in to Google Drive. You can close this tab.</body></html>"
            : $"<html><body>Google Drive sign-in failed: {WebUtility.HtmlEncode(query["error_description"] ?? error)}</body></html>";
        var buffer = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, cancellationToken);
        context.Response.Close();

        if (error is not null)
        {
            throw NotAuthenticated(
                $"Google Drive sign-in was cancelled or failed: {query["error_description"] ?? error}",
                LocalizedText.Of(StringKeys.Error.AuthSignInCancelled, "Google Drive", query["error_description"] ?? error));
        }

        if (string.IsNullOrEmpty(code) || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw NotAuthenticated("The sign-in redirect was missing its code or carried an unexpected state.", LocalizedText.Of(StringKeys.Error.AuthBadRedirect));
        }

        return code;
    }

    private static int ReserveLoopbackPort()
    {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        try
        {
            return ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        }
        finally
        {
            tcpListener.Stop();
        }
    }

    /// <summary>32 random bytes, base64url-encoded — well within PKCE's 43-128 character requirement. Exposed internally for a unit test to exercise the algorithm without a real browser.</summary>
    internal static string GeneratePkceVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal static string ComputePkceChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// <paramref name="message"/> is what reaches the CLI console and the crash log, so it stays
    /// English and greppable; <paramref name="detail"/> is what the interface shows
    /// (docs/PLAN-TECH-DEBT.md B6.5).
    /// </summary>
    private static DriveException NotAuthenticated(string message, LocalizedText detail)
        => new("Google Drive sign-in", exitCode: 1, stdout: string.Empty, stderr: message, message, DriveErrorKind.NotAuthenticated)
        {
            Detail = detail,
        };

    private static DriveException SignInTimedOut()
    {
        var minutes = SignInTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
        var message = $"Google Drive sign-in timed out after {minutes} minutes — no browser completed the login.";
        return new("Google Drive sign-in", exitCode: 1, stdout: string.Empty, stderr: message, message, DriveErrorKind.Timeout)
        {
            Detail = LocalizedText.Of(StringKeys.Error.AuthSignInTimeout, "Google Drive", minutes, "accounts.google.com"),
        };
    }
}
