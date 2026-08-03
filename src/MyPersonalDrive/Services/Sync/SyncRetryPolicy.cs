namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Decides whether a failed queue row gets another attempt and when — docs/PLAN-LOCAL-SYNC.md
/// §7's backoff schedule and error classification. Pure and clock-injected so the schedule is
/// unit-testable; jitter is supplied by the caller for the same reason.
/// </summary>
public static class SyncRetryPolicy
{
    /// <summary>§7's schedule: 5s, 15s, 45s, 2min, 5min — then the row is permanently Failed.</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    public static int MaxAttempts => Backoff.Length;

    /// <summary>
    /// When to try <paramref name="attemptsSoFar"/>+1, or null to give up. Null also means "give
    /// up immediately" for a permanent error, regardless of how many attempts are left: retrying
    /// an invalid filename or an exhausted quota just burns ~3.5s per CLI process to fail again.
    /// </summary>
    public static DateTimeOffset? NextAttemptAt(Exception exception, int attemptsSoFar, DateTimeOffset now, TimeSpan jitter = default)
    {
        if (!IsRetryable(exception))
        {
            return null;
        }

        if (attemptsSoFar >= MaxAttempts)
        {
            return null;
        }

        return now + Backoff[attemptsSoFar] + jitter;
    }

    /// <summary>
    /// Transient failures are worth retrying; everything else is not. A non-CLI exception
    /// (local IO, for instance) is treated as retryable-once-ish rather than permanent: a
    /// transient `IOException` from a file still being flushed is common, and the attempt cap
    /// bounds the damage of guessing wrong.
    /// </summary>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        CliException cli => cli.Kind switch
        {
            // Busy is the textbook retry case: the CLI lost a race on its own SQLite cache and
            // the same command will simply work next time (Appendix A #11).
            CliErrorKind.Network or CliErrorKind.Timeout or CliErrorKind.Busy or CliErrorKind.Unknown => true,

            // Auth and quota are real conditions that a retry cannot fix — they need the user.
            // §7 wants the whole pair paused for these; that's the scheduler's job in F3, so for
            // now they simply fail fast instead of burning five attempts.
            CliErrorKind.NotAuthenticated or CliErrorKind.Quota => false,

            // NotFound is genuinely ambiguous: the node moved or was trashed between the scan and
            // the transfer. Retrying the same path won't help — the next full scan will see the
            // new reality and plan correctly.
            CliErrorKind.NotFound => false,

            _ => false,
        },
        IOException => true,
        UnauthorizedAccessException => false,
        _ => false,
    };

    /// <summary>
    /// Whether this failure should stop the whole run rather than just fail one row. An expired
    /// session fails every subsequent action identically, ~3.5s at a time — there's no point
    /// working through a 400-item queue to produce 400 identical auth errors.
    /// </summary>
    public static bool ShouldAbortRun(Exception exception)
        => exception is CliException { Kind: CliErrorKind.NotAuthenticated or CliErrorKind.Quota };
}
