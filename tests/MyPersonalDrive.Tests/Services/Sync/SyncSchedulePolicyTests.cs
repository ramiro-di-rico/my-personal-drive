using System.Globalization;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncSchedulePolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00Z", CultureInfo.InvariantCulture);

    private static PairScheduleState State(
        DateTimeOffset? lastRunAt = null,
        TimeSpan? lastCycleDuration = null,
        int consecutiveErrors = 0,
        bool isDirty = false,
        bool isPaused = false)
        => new(lastRunAt, lastCycleDuration, consecutiveErrors, isDirty, isPaused);

    [Fact]
    public void WithNoMeasurementYet_TheIntervalIsTheFloor()
        => Assert.Equal(SyncSchedulePolicy.MinInterval, SyncSchedulePolicy.PollInterval(null));

    [Fact]
    public void AFastPair_StillPollsAtTheFloor()
    {
        // ~7s cycle × 10 = 70s, well under the 5-minute floor.
        Assert.Equal(SyncSchedulePolicy.MinInterval, SyncSchedulePolicy.PollInterval(TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void ASlowPair_BacksItselfOff_WithoutBeingConfigured()
    {
        // The Appendix A #11b case: a 50-folder tree is ~3 minutes of scanning per cycle, so a
        // fixed 5-minute poll would scan 3 minutes out of every 5. It earns ~30 minutes instead.
        Assert.Equal(TimeSpan.FromMinutes(30), SyncSchedulePolicy.PollInterval(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void AnAbsurdlySlowPair_IsStillCheckedHourly()
        => Assert.Equal(SyncSchedulePolicy.MaxInterval, SyncSchedulePolicy.PollInterval(TimeSpan.FromHours(5)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)] // clamped, and no overflow from a huge shift
    public void ErrorBackoffDoubles_ThenClamps(int consecutiveErrors, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SyncSchedulePolicy.ErrorBackoff(consecutiveErrors));

    [Fact]
    public void ANeverRunPair_IsDueImmediately()
        => Assert.True(SyncSchedulePolicy.ShouldRunNow(State(), T0));

    [Fact]
    public void APausedPair_IsNeverDue_EvenIfDirty()
        => Assert.False(SyncSchedulePolicy.ShouldRunNow(State(isDirty: true, isPaused: true), T0));

    [Fact]
    public void ADirtyPair_SkipsTheWait_BecauseTheDebounceAlreadyWaited()
    {
        var state = State(lastRunAt: T0, lastCycleDuration: TimeSpan.FromMinutes(3), isDirty: true);

        Assert.True(SyncSchedulePolicy.ShouldRunNow(state, T0));
    }

    [Fact]
    public void ACleanPair_WaitsOutItsEarnedInterval()
    {
        var state = State(lastRunAt: T0, lastCycleDuration: TimeSpan.FromSeconds(7));

        Assert.False(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(4)));
        Assert.True(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(5)));
    }

    [Fact]
    public void ADirtyButFailingPair_StillRespectsTheBackoff_SoItCannotSpin()
    {
        var state = State(lastRunAt: T0, isDirty: true, consecutiveErrors: 3); // 4-minute backoff

        Assert.False(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(3)));
        Assert.True(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(4)));
    }

    [Fact]
    public void TheBackoffWins_WhenItExceedsTheEarnedInterval()
    {
        // 7s cycle earns the 5-minute floor; 6 consecutive errors demand 30 minutes.
        var state = State(lastRunAt: T0, lastCycleDuration: TimeSpan.FromSeconds(7), consecutiveErrors: 6);

        Assert.False(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(29)));
        Assert.True(SyncSchedulePolicy.ShouldRunNow(state, T0.AddMinutes(30)));
    }
}
