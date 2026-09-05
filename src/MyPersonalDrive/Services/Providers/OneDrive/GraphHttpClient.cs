using System.Net;
using System.Net.Http.Headers;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// The one place every OneDrive operation actually calls Graph. Attaches the bearer token, retries
/// once on 401 after forcing a token refresh, honors <c>Retry-After</c> on 429/503, classifies
/// failures via <see cref="GraphErrorClassifier"/>, and raises <see cref="Activity"/> events so the
/// console shows each request the same way it shows a Proton CLI command
/// (docs/PLAN-CLOUD-PROVIDERS.md §4.2/§4.5). <see cref="HttpClient"/> ownership mirrors
/// <c>Providers.Proton.CliReleaseFeed</c>: injectable for tests, owned-and-disposed otherwise.
/// </summary>
public sealed class GraphHttpClient : IDisposable
{
    /// <summary>Applied when a 429/503 response carries no <c>Retry-After</c> header at all.</summary>
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(2);

    private readonly GraphAuthenticator _authenticator;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GraphHttpClient(GraphAuthenticator authenticator, HttpClient? httpClient = null)
    {
        _authenticator = authenticator;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    }

    public event EventHandler<ProviderActivity>? Activity;

    /// <summary>
    /// Sends an authenticated request, retrying at most once on a 401 (after forcing a refresh) and
    /// at most once on a 429/503 (after honoring <c>Retry-After</c>). <paramref name="requestFactory"/>
    /// builds a fresh <see cref="HttpRequestMessage"/> per attempt — a disposed request/content
    /// can't be resent, and a chunked-upload body needs a fresh stream each time anyway.
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
    /// Sends a request to a pre-authenticated URL (a download's 302 target, an upload session's
    /// <c>uploadUrl</c>) — these must NOT carry the bearer header (docs/PLAN-CLOUD-PROVIDERS.md
    /// §4.3), and don't go through the 401-refresh dance since Graph itself doesn't own them.
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
            throw new DriveException("Graph request", exitCode: 1, stdout: string.Empty, stderr: ex.Message, ex.Message, DriveErrorKind.Network);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DriveException("Graph request", exitCode: 1, stdout: string.Empty, stderr: ex.Message, "La solicitud a OneDrive superó el tiempo de espera.", DriveErrorKind.Timeout);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && !retriedAuth)
        {
            response.Dispose();
            await _authenticator.ForceRefreshAsync(cancellationToken);
            return await SendWithRetriesAsync(requestFactory, retriedAuth: true, retriedRateLimit, cancellationToken);
        }

        if ((response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable) && !retriedRateLimit)
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
            var kind = GraphErrorClassifier.Classify(statusCode, body);
            var message = ExtractMessage(body) ?? $"OneDrive request failed with status {(int)statusCode}.";
            var requestDescription = request.RequestUri?.ToString() ?? "Graph request";
            response.Dispose();
            throw new DriveException(requestDescription, (int)statusCode, stdout: string.Empty, stderr: body, message, kind);
        }

        return response;
    }

    private static string? ExtractMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize(responseBody, AppJsonContext.Default.GraphErrorEnvelope);
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
