using System.Net;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>The status/code → DriveErrorKind table from docs/PLAN-CLOUD-PROVIDERS.md §4.7.</summary>
public class GraphErrorClassifierTests
{
    private static string ErrorBody(string code) => """{"error":{"code":"CODE","message":"boom"}}""".Replace("CODE", code);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", DriveErrorKind.NotAuthenticated)]
    [InlineData(HttpStatusCode.Forbidden, "accessDenied", DriveErrorKind.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, "itemNotFound", DriveErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, "invalidRequest", DriveErrorKind.InvalidArgument)]
    [InlineData(HttpStatusCode.TooManyRequests, "activityLimitReached", DriveErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", DriveErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InsufficientStorage, "quotaLimitReached", DriveErrorKind.Quota)]
    public void MapsStatusCodeToTheDocumentedKind(HttpStatusCode statusCode, string code, DriveErrorKind expected)
    {
        Assert.Equal(expected, GraphErrorClassifier.Classify(statusCode, ErrorBody(code)));
    }

    [Fact]
    public void A409WithNameAlreadyExists_IsAlreadyExists_NotJustConflict()
    {
        Assert.Equal(DriveErrorKind.AlreadyExists, GraphErrorClassifier.Classify(HttpStatusCode.Conflict, ErrorBody("nameAlreadyExists")));
    }

    [Fact]
    public void A409WithAnotherCode_IsConflict_DistinctFromAlreadyExists()
    {
        Assert.Equal(DriveErrorKind.Conflict, GraphErrorClassifier.Classify(HttpStatusCode.Conflict, ErrorBody("resourceModified")));
    }

    [Fact]
    public void AMalformedBody_DegradesToUnknown_InsteadOfThrowing()
    {
        Assert.Equal(DriveErrorKind.Unknown, GraphErrorClassifier.Classify(HttpStatusCode.InternalServerError, "not json at all"));
    }

    [Fact]
    public void AnEmptyBody_DegradesToUnknown()
    {
        Assert.Equal(DriveErrorKind.Unknown, GraphErrorClassifier.Classify(HttpStatusCode.InternalServerError, string.Empty));
    }
}
