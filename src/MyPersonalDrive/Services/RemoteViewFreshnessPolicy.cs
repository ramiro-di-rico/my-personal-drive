namespace MyPersonalDrive.Services;

/// <summary>
/// Decides when the file browser should throw away the CLI's cached view of the remote tree.
///
/// The problem it answers (docs/PLAN-LOCAL-SYNC.md Appendix A #16): `filesystem list` is served from
/// a cache the CLI never revalidates, so a folder whose cached children went stale stays wrong
/// indefinitely — verified on a real account, where a folder listed 17 children warm and 21 cold,
/// the four missing ones being real. Refreshing only on demand would leave a user staring at a
/// listing that is silently incomplete; refreshing on every navigation would pay a cold start
/// (~2× the usual ~3.5s) per click and, because the discard is global, would repeatedly strip the
/// cache from a sync scan running at the same time.
///
/// Pure and clock-injected so the window is testable without waiting for it.
/// </summary>
public sealed class RemoteViewFreshnessPolicy
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _window;
    private DateTimeOffset? _refreshedAt;

    public RemoteViewFreshnessPolicy(TimeSpan? window = null)
    {
        _window = window ?? DefaultWindow;
    }

    /// <summary>
    /// Whether to discard the CLI's cache before the next listing, recording the decision when the
    /// answer is yes.
    /// </summary>
    /// <param name="force">
    /// The user's own Refresh. Always refreshes: pressing Refresh and being handed the same cached
    /// answer is the one outcome that makes the button meaningless.
    /// </param>
    public bool ShouldRefresh(DateTimeOffset now, bool force)
    {
        if (!force && _refreshedAt is { } last && now - last < _window)
        {
            return false;
        }

        // Stamped here rather than after the discard completes, so a discard that throws can't leave
        // every following navigation retrying it. The cost of dropping one is a stale listing for
        // the rest of the window — exactly the exposure the window already accepts.
        _refreshedAt = now;
        return true;
    }
}
