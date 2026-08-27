using System.Net;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Routes requests by a predicate over method+URL to a canned response, in registration order —
/// the first matching route wins, so tests register the specific case before a catch-all. Records
/// every request so a test can assert on what was actually sent (headers, body, retry count).
/// Mirrors <c>FakeCliExecutor</c>'s "records + canned response" shape for the HTTP transport.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(HttpMethod Method, string Url, string? AuthorizationHeader, string? Body);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public void When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _routes.Add((match, respond));

    public void When(HttpMethod method, string urlContains, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => When(request => request.Method == method && (request.RequestUri?.ToString().Contains(urlContains, StringComparison.Ordinal) ?? false), respond);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        // AbsoluteUri, not ToString(): Uri.ToString() decodes percent-encoded "safe" characters
        // (a space stays visible as %20 in AbsoluteUri but comes back as a literal space from
        // ToString()) — tests asserting on path encoding need the wire form, not the display form.
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, request.Headers.Authorization?.ToString(), body));

        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                return respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"FakeHttpMessageHandler: no route registered for {request.Method} {request.RequestUri}"),
        };
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
