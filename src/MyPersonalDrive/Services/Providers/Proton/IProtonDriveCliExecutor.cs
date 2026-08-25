using MyPersonalDrive.Services;

namespace MyPersonalDrive.Services.Providers.Proton;

public interface IProtonDriveCliExecutor
{
    event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <param name="arguments">
    /// Each element becomes one process argument via <c>ProcessStartInfo.ArgumentList</c>,
    /// which lets the runtime apply platform-correct escaping. Never build a single
    /// pre-quoted string: it cannot round-trip names containing quotes or backslashes.
    /// </param>
    /// <param name="timeout">
    /// Null uses the executor's default timeout. Pass <see cref="Timeout.InfiniteTimeSpan"/>
    /// for commands that wait on user interaction (e.g. a browser-based login).
    /// </param>
    Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null);

    /// <summary>
    /// Discards whatever the CLI has cached about the remote tree, so the next listing is answered
    /// by the server rather than from disk. Call once at the start of a remote scan, never per
    /// folder: within a single scan the cache is what keeps the walk cheap.
    ///
    /// <b>Why this exists at all.</b> `filesystem list` is cache-authoritative — once a folder's
    /// children are cached the CLI never re-queries the API for them, and freshness rides entirely
    /// on an event subscription that a ~3.5s process is in no position to receive. Verified on a
    /// real account (docs/PLAN-LOCAL-SYNC.md Appendix A #16): a folder listed 17 children from the
    /// warm cache and 21 from a cold one, the four missing nodes being real and years old, and the
    /// warm answer never healed on its own. A scan that starts from a stale cache reports nodes as
    /// absent, which in a TwoWay pair reads as a remote deletion.
    /// </summary>
    Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default);
}
