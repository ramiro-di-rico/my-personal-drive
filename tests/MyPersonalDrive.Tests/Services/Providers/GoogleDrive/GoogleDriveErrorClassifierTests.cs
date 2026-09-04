using System.Net;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.GoogleDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.GoogleDrive;

/// <summary>The status/reason → DriveErrorKind table from docs/PLAN-CLOUD-PROVIDERS.md §8.7, using Drive's real v3 error body shape.</summary>
public class GoogleDriveErrorClassifierTests
{
    private static string ErrorBody(string reason)
        => """{"error":{"code":403,"message":"boom","errors":[{"reason":"REASON","domain":"usageLimits"}]}}""".Replace("REASON", reason);

    [Fact]
    public void A401_IsNotAuthenticated()
    {
        Assert.Equal(DriveErrorKind.NotAuthenticated, GoogleDriveErrorClassifier.Classify(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"boom","errors":[{"reason":"authError"}]}}"""));
    }

    [Fact]
    public void A404_IsNotFound()
    {
        Assert.Equal(DriveErrorKind.NotFound, GoogleDriveErrorClassifier.Classify(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"boom","errors":[{"reason":"notFound"}]}}"""));
    }

    [Fact]
    public void A429_IsRateLimited_RegardlessOfBody()
    {
        Assert.Equal(DriveErrorKind.RateLimited, GoogleDriveErrorClassifier.Classify((HttpStatusCode)429, string.Empty));
    }

    [Theory]
    [InlineData("rateLimitExceeded")]
    [InlineData("userRateLimitExceeded")]
    public void A403WithARateLimitReason_IsRateLimited(string reason)
    {
        Assert.Equal(DriveErrorKind.RateLimited, GoogleDriveErrorClassifier.Classify(HttpStatusCode.Forbidden, ErrorBody(reason)));
    }

    [Fact]
    public void A403WithStorageQuotaExceeded_IsQuota()
    {
        Assert.Equal(DriveErrorKind.Quota, GoogleDriveErrorClassifier.Classify(HttpStatusCode.Forbidden, ErrorBody("storageQuotaExceeded")));
    }

    [Fact]
    public void A403WithInsufficientFilePermissions_IsPermissionDenied()
    {
        Assert.Equal(DriveErrorKind.PermissionDenied, GoogleDriveErrorClassifier.Classify(HttpStatusCode.Forbidden, ErrorBody("insufficientFilePermissions")));
    }

    [Fact]
    public void A403WithAnUnrecognizedReason_DegradesToPermissionDenied()
    {
        Assert.Equal(DriveErrorKind.PermissionDenied, GoogleDriveErrorClassifier.Classify(HttpStatusCode.Forbidden, ErrorBody("somethingElseEntirely")));
    }

    [Fact]
    public void AMalformedBody_DegradesToUnknown_InsteadOfThrowing()
    {
        Assert.Equal(DriveErrorKind.Unknown, GoogleDriveErrorClassifier.Classify(HttpStatusCode.InternalServerError, "not json at all"));
    }

    [Fact]
    public void AnEmptyBody_DegradesToUnknown()
    {
        Assert.Equal(DriveErrorKind.Unknown, GoogleDriveErrorClassifier.Classify(HttpStatusCode.InternalServerError, string.Empty));
    }

    [Fact]
    public void AnErrorsArrayThatIsAbsent_StillClassifiesFromStatusAlone()
    {
        Assert.Equal(DriveErrorKind.NotFound, GoogleDriveErrorClassifier.Classify(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"boom"}}"""));
    }
}
