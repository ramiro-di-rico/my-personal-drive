using System.Net;
using System.Text.Json;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Maps a Graph HTTP response to <see cref="DriveErrorKind"/>. Reads the structured
/// <c>{"error":{"code":…}}</c> body Graph always returns on failure, not a message substring — the
/// thing <c>Providers.Proton.CliErrorClassifier</c> can't do because the CLI has no structured
/// errors. See docs/PLAN-CLOUD-PROVIDERS.md §4.7.
/// </summary>
public static class GraphErrorClassifier
{
    public static DriveErrorKind Classify(HttpStatusCode statusCode, string responseBody)
    {
        var code = ExtractErrorCode(responseBody);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => DriveErrorKind.NotAuthenticated,
            HttpStatusCode.Forbidden => DriveErrorKind.PermissionDenied,
            HttpStatusCode.NotFound => DriveErrorKind.NotFound,
            HttpStatusCode.Conflict => string.Equals(code, "nameAlreadyExists", StringComparison.OrdinalIgnoreCase)
                ? DriveErrorKind.AlreadyExists
                : DriveErrorKind.Conflict,
            HttpStatusCode.BadRequest => DriveErrorKind.InvalidArgument,
            HttpStatusCode.TooManyRequests => DriveErrorKind.RateLimited,
            HttpStatusCode.ServiceUnavailable => DriveErrorKind.RateLimited,
            HttpStatusCode.InsufficientStorage => DriveErrorKind.Quota,
            _ when string.Equals(code, "quotaLimitReached", StringComparison.OrdinalIgnoreCase) => DriveErrorKind.Quota,
            _ => DriveErrorKind.Unknown,
        };
    }

    /// <summary>
    /// Reads <c>error.code</c> out of a Graph error body. Returns null for anything that isn't the
    /// documented shape — a classifier must degrade to <see cref="DriveErrorKind.Unknown"/> on a
    /// malformed body, never throw while already handling one error.
    /// </summary>
    private static string? ExtractErrorCode(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
