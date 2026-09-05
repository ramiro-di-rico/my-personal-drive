using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Sign-in/out for OneDrive: authorization-code + PKCE via a loopback listener, no MSAL — about
/// 150 lines of <see cref="HttpClient"/> against `login.microsoftonline.com`, per
/// docs/PLAN-CLOUD-PROVIDERS.md §4.2. No device-code fallback in this pass (documented gap: a
/// machine with no usable browser has no way to sign in). No separate <c>IAuthPrompt</c>
/// abstraction either — <see cref="IDriveAuthenticator"/> is deliberately minimal today, and a
/// second implementation is what would justify extracting one.
/// </summary>
public sealed class GraphAuthenticator : IDriveAuthenticator
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string Scopes = "Files.ReadWrite.All offline_access User.Read";
    private const string MeEndpoint = "https://graph.microsoft.com/v1.0/me";

    /// <summary>Refresh this far ahead of the stored expiry, so a request in flight never races an about-to-expire token.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly string _clientId;
    private readonly OneDriveTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public GraphAuthenticator(string clientId, OneDriveTokenStore tokenStore, HttpClient? httpClient = null)
    {
        _clientId = clientId;
        _tokenStore = tokenStore;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Raises the same <see cref="ProviderActivity"/> shape as the CLI-based providers, so the console shows the authorize URL and the sign-in outcome.</summary>
    public event EventHandler<ProviderActivity>? Activity;

    public string? AccountLabel => _tokenStore.Load()?.AccountLabel;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new InvalidOperationException("No hay un client ID de OneDrive configurado. Cargá uno en Configuración antes de iniciar sesión.");
        }

        var verifier = GeneratePkceVerifier();
        var challenge = ComputePkceChallenge(verifier);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        // HttpListener has no "give me any free port" mode of its own (unlike TcpListener's port
        // 0), so a free port is reserved with a throwaway TcpListener first, then handed to
        // HttpListener by exact port number. A small bind race is possible between the two but
        // is the standard workaround for this exact loopback-OAuth scenario. The redirect URI
        // Azure needs registered is the port-less "http://localhost" — Microsoft's endpoint
        // matches that registration regardless of which actual port is used
        // (docs/PLAN-CLOUD-PROVIDERS.md §4.2).
        var port = ReserveLoopbackPort();
        using var listener = new HttpListener();
        // "localhost", not "127.0.0.1": Azure's port-less "http://localhost" registration for a
        // public client only matches requests using that literal hostname, whatever the port.
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var redirectUri = $"http://localhost:{port}/";

        var authorizeUrl = BuildAuthorizeUrl(redirectUri, challenge, state);
        Activity?.Invoke(this, new ProviderActivity(ActivityKind.Started, $"GET {AuthorizeEndpoint}", Text: authorizeUrl, IsError: false, ExitCode: null, Duration: null));

        TryLaunchBrowser(authorizeUrl);

        try
        {
            var code = await WaitForRedirectAsync(listener, state, cancellationToken);
            var token = await ExchangeCodeForTokenAsync(code, verifier, redirectUri, cancellationToken);
            var accountLabel = await TryFetchAccountLabelAsync(token.AccessToken, cancellationToken);

            _tokenStore.Save(new StoredOneDriveToken
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken ?? string.Empty,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                AccountLabel = accountLabel,
            });

            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, $"GET {AuthorizeEndpoint}", Text: null, IsError: false, ExitCode: 0, Duration: null));
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
    /// when there is no stored token or the refresh itself fails — the one path every operation
    /// method funnels through before making a request.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var stored = _tokenStore.Load();
        if (stored is null)
        {
            throw NotAuthenticated("No hay una sesión de OneDrive guardada.");
        }

        if (stored.ExpiresAt - DateTimeOffset.UtcNow > RefreshMargin)
        {
            return stored.AccessToken;
        }

        return await RefreshAsync(stored, cancellationToken);
    }

    /// <summary>Forces a refresh regardless of cached expiry — the 401-retry-once path in <see cref="GraphHttpClient"/>.</summary>
    public async Task<string> ForceRefreshAsync(CancellationToken cancellationToken = default)
    {
        var stored = _tokenStore.Load() ?? throw NotAuthenticated("No hay una sesión de OneDrive guardada.");
        return await RefreshAsync(stored, cancellationToken);
    }

    private async Task<string> RefreshAsync(StoredOneDriveToken stored, CancellationToken cancellationToken)
    {
        // Concurrent requests hitting a 401 at the same time must not each spend the one refresh
        // token — the second refresh attempt with an already-consumed token would itself fail.
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have already refreshed while this one waited on the gate — but
            // only skip the network call if the stored access token has actually changed since
            // `stored` was read. Checking expiry instead would be wrong here: ForceRefreshAsync
            // calls this after the server itself returned 401, which can happen well before the
            // token's cached expiry (revoked, wrong audience, clock skew) — an expiry check would
            // then skip the real refresh and hand back the same rejected token.
            var current = _tokenStore.Load();
            if (current is not null && !string.Equals(current.AccessToken, stored.AccessToken, StringComparison.Ordinal))
            {
                return current.AccessToken;
            }

            if (string.IsNullOrEmpty(stored.RefreshToken))
            {
                throw NotAuthenticated("No hay un refresh token guardado; iniciá sesión de nuevo.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = stored.RefreshToken,
                    ["scope"] = Scopes,
                }),
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = ParseTokenResponse(body, response.IsSuccessStatusCode);

            var accountLabel = stored.AccountLabel ?? await TryFetchAccountLabelAsync(token.AccessToken, cancellationToken);
            var refreshed = new StoredOneDriveToken
            {
                AccessToken = token.AccessToken,
                // Graph's refresh response doesn't always include a new refresh token; keep the old
                // one when it doesn't, per OAuth2 refresh-token-rotation conventions.
                RefreshToken = token.RefreshToken ?? stored.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                AccountLabel = accountLabel,
            };
            _tokenStore.Save(refreshed);
            return refreshed.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            throw NotAuthenticated($"No se pudo renovar la sesión de OneDrive: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<GraphTokenResponse> ExchangeCodeForTokenAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["scope"] = Scopes,
            }),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTokenResponse(body, response.IsSuccessStatusCode);
    }

    private static GraphTokenResponse ParseTokenResponse(string body, bool wasSuccessStatus)
    {
        GraphTokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize(body, AppJsonContext.Default.GraphTokenResponse);
        }
        catch (JsonException)
        {
            throw NotAuthenticated("El endpoint de tokens devolvió una respuesta que no se pudo interpretar.");
        }

        if (token is null || !wasSuccessStatus || string.IsNullOrEmpty(token.AccessToken))
        {
            var reason = token?.ErrorDescription ?? token?.Error ?? "unknown error";
            throw NotAuthenticated($"Falló el inicio de sesión de OneDrive: {reason}");
        }

        return token;
    }

    private async Task<string?> TryFetchAccountLabelAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var user = JsonSerializer.Deserialize(body, AppJsonContext.Default.GraphUser);
            return user?.Mail ?? user?.UserPrincipalName ?? user?.DisplayName;
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
        query["scope"] = Scopes;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
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
            ? "<html><body>Signed in to OneDrive. You can close this tab.</body></html>"
            : $"<html><body>OneDrive sign-in failed: {WebUtility.HtmlEncode(query["error_description"] ?? error)}</body></html>";
        var buffer = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, cancellationToken);
        context.Response.Close();

        if (error is not null)
        {
            throw NotAuthenticated($"El inicio de sesión de OneDrive se canceló o falló: {query["error_description"] ?? error}");
        }

        if (string.IsNullOrEmpty(code) || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw NotAuthenticated("A la redirección de inicio de sesión le faltaba el código o traía un estado inesperado — posible CSRF, no se continúa.");
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

    private static string GeneratePkceVerifier()
    {
        // 32 random bytes, base64url-encoded — well within PKCE's 43-128 character requirement.
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private static string ComputePkceChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DriveException NotAuthenticated(string message)
        => new("OneDrive sign-in", exitCode: 1, stdout: string.Empty, stderr: message, message, DriveErrorKind.NotAuthenticated);
}
