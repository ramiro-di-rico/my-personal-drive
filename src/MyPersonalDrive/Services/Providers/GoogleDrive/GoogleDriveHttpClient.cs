using System.Net;
using System.Net.Http.Headers;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// The one place every Google Drive operation actually calls the Drive v3 API. Attaches the bearer
/// token, retries once on 401 after forcing a token refresh, honors <c>Retry-After</c> on 429 and on
/// a 403 whose body's <c>error.errors[0].reason</c> is <c>rateLimitExceeded</c>/<c>userRateLimitExceeded</c>
/// (docs/PLAN-CLOUD-PROVIDERS.md §8.5/§8.7), classifies failures via
/// <see cref="GoogleDriveErrorClassifier"/>, and raises <see cref="Activity"/> events so the console
/// shows each request the same way it shows every other provider's. Exact structural mirror of
/// <c>OneDrive.GraphHttpClient</c>.
/// </summary>
public sealed class GoogleDriveHttpClient : IDisposable
{
    public const string BaseUrl = "https://www.googleapis.com/drive/v3/";
    public const string UploadBaseUrl = "https://www.googleapis.com/upload/drive/v3/";

    /// <summary>Applied when a 429/rate-limited 403 response carries no <c>Retry-After</c> header at all.</summary>
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(2);

    private readonly GoogleDriveAuthenticator _authenticator;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GoogleDriveHttpClient(GoogleDriveAuthenticator authenticator, HttpClient? httpClient = null)
    {
        _authenticator = authenticator;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    }

    public event EventHandler<ProviderActivity>? Activity;

    /// <summary>
    /// Sends an authenticated request, retrying at most once on a 401 (after forcing a refresh) and
    /// at most once on a rate-limit response (after honoring <c>Retry-After</c>).
    /// <paramref name="requestFactory"/> builds a fresh <see cref="HttpRequestMessage"/> per
    /// attempt — a disposed request/content can't be resent.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(string label, Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default)
    {
        Activity?.Invoke(this, new ProviderActivity(ActivityKind.Started, label, Text: null, IsError: false, ExitCode: null, Duration: null));
        var startedAt = TimeProvider.System.GetTimestamp();

        try
        {
            var response = await SendWithRetriesAsync(requestFactory, retriedAuth: false, retriedRateLimit: false, cancellationToken);
            var duration = TimeProvider.System.GetElapsedTime(startedAt);
            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, label, Text: null, IsError: !response.IsSuccessStatusCode, (int)response.StatusCode, duration));
            return response;
        }
        catch (Exception)
        {
            var duration = TimeProvider.System.GetElapsedTime(startedAt);
            Activity?.Invoke(this, new ProviderActivity(ActivityKind.Finished, label, Text: null, IsError: true, ExitCode: null, duration));
            throw;
        }
    }

    /// <summary>
    /// Sends a request to a resumable-upload session URI — pre-authenticated by Drive per its own
    /// documented contract the moment the session was created, so it must NOT carry a fresh bearer
    /// header, and it needs no 401-refresh handling since Drive's core API doesn't own that URL.
    /// </summary>
    public Task<HttpResponseMessage> SendUnauthenticatedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        => _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetriesAsync(Func<HttpRequestMessage> requestFactory, bool retriedAuth, bool retriedRateLimit, CancellationToken cancellationToken)
    {
        var request = requestFactory();
        var token = await _authenticator.GetValidAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new DriveException("Google Drive request", exitCode: 1, stdout: string.Empty, stderr: ex.Message, ex.Message, DriveErrorKind.Network);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DriveException("Google Drive request", exitCode: 1, stdout: string.Empty, stderr: ex.Message, "La solicitud a Google Drive superó el tiempo de espera.", DriveErrorKind.Timeout);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && !retriedAuth)
        {
            response.Dispose();
            await _authenticator.ForceRefreshAsync(cancellationToken);
            return await SendWithRetriesAsync(requestFactory, retriedAuth: true, retriedRateLimit, cancellationToken);
        }

        if (!retriedRateLimit && await IsRateLimitedAsync(response, cancellationToken) is not null)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? DefaultRateLimitDelay;
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
            return await SendWithRetriesAsync(requestFactory, retriedAuth, retriedRateLimit: true, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var kind = GoogleDriveErrorClassifier.Classify(statusCode, body);
            var message = ExtractMessage(body) ?? $"Google Drive request failed with status {(int)statusCode}.";
            var requestDescription = request.RequestUri?.ToString() ?? "Google Drive request";
            response.Dispose();
            throw new DriveException(requestDescription, (int)statusCode, stdout: string.Empty, stderr: body, message, kind);
        }

        return response;
    }

    /// <summary>
    /// True for a bare 429, or a 403 whose body's <c>error.errors[0].reason</c> is
    /// <c>rateLimitExceeded</c>/<c>userRateLimitExceeded</c> (docs/PLAN-CLOUD-PROVIDERS.md §8.7) —
    /// the body has to be read either way to tell a rate-limited 403 apart from a permission-denied
    /// one, so this reads it once and hands it back for reuse rather than reading it twice.
    /// </summary>
    private static async Task<string?> IsRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return string.Empty;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return GoogleDriveErrorClassifier.Classify(response.StatusCode, body) == DriveErrorKind.RateLimited ? body : null;
    }

    private static string? ExtractMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize(responseBody, AppJsonContext.Default.GoogleDriveErrorEnvelope);
            return envelope?.Error?.Message;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
