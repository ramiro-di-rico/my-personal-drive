using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncRetryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-01T12:00:00Z");

    private static DriveException Cli(DriveErrorKind kind)
        => new("filesystem download /x /y", 1, "", "boom", "boom", kind);

    [Fact]
    public void Backoff_FollowsThePlansSchedule()
        => Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)],
            SyncRetryPolicy.Backoff);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, 45)]
    public void NextAttempt_UsesTheAttemptCountToPickTheDelay(int attemptsSoFar, int expectedSeconds)
        => Assert.Equal(Now.AddSeconds(expectedSeconds), SyncRetryPolicy.NextAttemptAt(Cli(DriveErrorKind.Network), attemptsSoFar, Now));

    [Fact]
    public void NextAttempt_AfterTheLastAttempt_GivesUp()
        => Assert.Null(SyncRetryPolicy.NextAttemptAt(Cli(DriveErrorKind.Network), SyncRetryPolicy.MaxAttempts, Now));

    [Fact]
    public void NextAttempt_AddsTheCallersJitter()
        => Assert.Equal(Now.AddSeconds(5).AddMilliseconds(250),
            SyncRetryPolicy.NextAttemptAt(Cli(DriveErrorKind.Network), 0, Now, TimeSpan.FromMilliseconds(250)));

    [Theory]
    [InlineData(DriveErrorKind.Network)]
    [InlineData(DriveErrorKind.Timeout)]
    [InlineData(DriveErrorKind.Unknown)]
    public void Retryable_TransientCliFailures(DriveErrorKind kind)
        => Assert.True(SyncRetryPolicy.IsRetryable(Cli(kind)));

    [Theory]
    [InlineData(DriveErrorKind.NotAuthenticated)]
    [InlineData(DriveErrorKind.Quota)]
    [InlineData(DriveErrorKind.NotFound)]
    [InlineData(DriveErrorKind.PermissionDenied)]
    [InlineData(DriveErrorKind.InvalidArgument)]
    public void NotRetryable_FailuresARetryCannotFix(DriveErrorKind kind)
    {
        Assert.False(SyncRetryPolicy.IsRetryable(Cli(kind)));
        Assert.Null(SyncRetryPolicy.NextAttemptAt(Cli(kind), attemptsSoFar: 0, Now));
    }

    [Fact]
    public void Retryable_LocalIoIsWorthAnotherTry_ButAPermissionProblemIsNot()
    {
        Assert.True(SyncRetryPolicy.IsRetryable(new IOException("file in use")));
        Assert.False(SyncRetryPolicy.IsRetryable(new UnauthorizedAccessException()));
    }

    [Fact]
    public void AbortsTheRun_OnlyForConditionsThatWouldFailEveryRemainingAction()
    {
        Assert.True(SyncRetryPolicy.ShouldAbortRun(Cli(DriveErrorKind.NotAuthenticated)));
        Assert.True(SyncRetryPolicy.ShouldAbortRun(Cli(DriveErrorKind.Quota)));
        Assert.False(SyncRetryPolicy.ShouldAbortRun(Cli(DriveErrorKind.Network)));
        Assert.False(SyncRetryPolicy.ShouldAbortRun(Cli(DriveErrorKind.NotFound)));
        Assert.False(SyncRetryPolicy.ShouldAbortRun(new IOException()));
    }
}
