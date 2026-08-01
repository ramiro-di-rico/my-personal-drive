namespace MyPersonalDrive.Services.Sync;

/// <summary>How a pair's next automatic cycle is timed. Pure and clock-injected, like the reconciler.</summary>
public sealed record PairScheduleState(
    DateTimeOffset? LastRunAt,
    TimeSpan? LastCycleDuration,
    int ConsecutiveErrors,
    bool IsDirty,
    bool IsPaused);

/// <summary>
/// Decides *when* a pair should next run — docs/PLAN-LOCAL-SYNC.md §6.4, rewritten after
/// Appendix A #11b.
///
/// The plan originally specified a fixed 5-minute poll. That is unusable here: #11b established
/// that a remote scan cannot be incremental (no change signal propagates, so every cycle must walk
/// the whole tree at ~3.5s per folder). A 50-folder pair takes ~3 minutes to scan, so a 5-minute
/// timer would leave it scanning three minutes out of every five, forever.
///
/// So the interval is <b>derived from what this pair actually costs to sync</b>: ten times the last
/// observed cycle duration, floored and capped. Ten gives a ~10% duty cycle — small pairs still poll
/// at the floor, and a large tree backs itself off without the user configuring anything.
///
/// The measurement is the *whole* cycle (scan + transfers), not the scan alone. That's deliberate:
/// the thing worth bounding is total work, and a pair that spends five minutes transferring has
/// earned a longer rest just as much as one that spends five minutes scanning.
/// </summary>
public static class SyncSchedulePolicy
{
    /// <summary>Never poll more often than this, however fast the pair scans.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Never poll less often than this, however slow it scans. A pair so large that 10× its scan
    /// exceeds an hour is better served by "Sync now" than by a timer, but silently never checking
    /// would be worse than checking hourly.
    /// </summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    /// <summary>Target ratio of idle time to scan time — the 10% duty cycle.</summary>
    public const int ScanDurationMultiplier = 10;

    /// <summary>Ceiling for the consecutive-error backoff (§6.4).</summary>
    public static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The interval this pair has earned, from its own measured scan cost. Falls back to
    /// <see cref="MinInterval"/> until a first scan has been timed.
    /// </summary>
    public static TimeSpan PollInterval(TimeSpan? lastCycleDuration)
    {
        if (lastCycleDuration is not { } duration || duration <= TimeSpan.Zero)
        {
            return MinInterval;
        }

        var scaled = duration * ScanDurationMultiplier;
        return scaled < MinInterval ? MinInterval : scaled > MaxInterval ? MaxInterval : scaled;
    }

    /// <summary>
    /// Backoff after consecutive failures: 1, 2, 4, 8, 16, 30, 30… minutes. Zero when healthy.
    /// </summary>
    public static TimeSpan ErrorBackoff(int consecutiveErrors)
    {
        if (consecutiveErrors <= 0)
        {
            return TimeSpan.Zero;
        }

        // Cap the shift before it can overflow, then clamp the result.
        var doublings = Math.Min(consecutiveErrors - 1, 10);
        var backoff = TimeSpan.FromMinutes(1) * Math.Pow(2, doublings);
        return backoff > MaxErrorBackoff ? MaxErrorBackoff : backoff;
    }

    /// <summary>
    /// When this pair may next run. A pair the watcher flagged dirty is due immediately — the
    /// debounce in <see cref="ChangeDebouncer"/> has already waited for the writes to settle, so
    /// making it wait out a poll interval as well would just add latency to every local edit.
    /// A dirty pair still respects the error backoff, so a broken pair can't spin.
    /// </summary>
    public static DateTimeOffset NextDueAt(PairScheduleState state)
    {
        if (state.LastRunAt is not { } lastRunAt)
        {
            return DateTimeOffset.MinValue; // never run: due now
        }

        var backoff = ErrorBackoff(state.ConsecutiveErrors);
        if (state.IsDirty)
        {
            return lastRunAt + backoff;
        }

        var interval = PollInterval(state.LastCycleDuration);
        return lastRunAt + (backoff > interval ? backoff : interval);
    }

    public static bool ShouldRunNow(PairScheduleState state, DateTimeOffset now)
        => !state.IsPaused && now >= NextDueAt(state);
}
