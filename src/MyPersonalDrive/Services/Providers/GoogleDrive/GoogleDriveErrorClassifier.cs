using System.Net;
using System.Text.Json;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Maps a Drive v3 HTTP response to <see cref="DriveErrorKind"/>, per the table in
/// docs/PLAN-CLOUD-PROVIDERS.md §8.7. Reads the structured
/// <c>{"error":{"code":…,"errors":[{"reason":…}]}}</c> body Drive returns on failure — classifying
/// on <c>error.errors[0].reason</c> (machine-readable), not the human <c>message</c>, mirroring the
/// rule <c>OneDrive.GraphErrorClassifier</c> follows for Graph's own <c>error.code</c>. Falls back
/// to the top-level <c>error.code</c>/HTTP status alone when the <c>errors</c> array is absent or
/// malformed — never throws while already handling one error, same defensive shape
/// <c>GraphErrorClassifier.ExtractErrorCode</c> uses.
/// </summary>
public static class GoogleDriveErrorClassifier
{
    public static DriveErrorKind Classify(HttpStatusCode statusCode, string responseBody)
    {
        var reason = ExtractReason(responseBody);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => DriveErrorKind.NotAuthenticated,
            HttpStatusCode.NotFound => DriveErrorKind.NotFound,
            HttpStatusCode.TooManyRequests => DriveErrorKind.RateLimited,
            HttpStatusCode.Forbidden => reason switch
            {
                "rateLimitExceeded" or "userRateLimitExceeded" => DriveErrorKind.RateLimited,
                "storageQuotaExceeded" => DriveErrorKind.Quota,
                "insufficientFilePermissions" => DriveErrorKind.PermissionDenied,
                _ => DriveErrorKind.PermissionDenied,
            },
            _ => DriveErrorKind.Unknown,
        };
    }

    /// <summary>
    /// Reads <c>error.errors[0].reason</c> out of a Drive v3 error body. Returns null for anything
    /// that isn't the documented shape — a classifier must degrade to <see cref="DriveErrorKind.Unknown"/>
    /// (or, here, to <see cref="DriveErrorKind.PermissionDenied"/>/<see cref="DriveErrorKind.RateLimited"/>
    /// from status code alone) on a malformed body, never throw while already handling one error.
    /// </summary>
    private static string? ExtractReason(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (!error.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            {
                return null;
            }

            var first = errors[0];
            return first.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
