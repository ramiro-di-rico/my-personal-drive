using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Remembers, for a short window, what this engine just did to a node, so that observations which
/// merely reflect our own action aren't mistaken for someone else's change. One mechanism for both
/// halves of docs/PLAN-LOCAL-SYNC.md's echo problem — but deliberately <b>two separate registers</b>,
/// because the two halves need opposite treatment:
///
/// <list type="bullet">
/// <item><b>Deletions</b> (<see cref="SuppressDeletion"/> → <see cref="Filter"/>): a scan that still
/// reports a node we deleted is <i>wrong</i>, so the entry is removed from the scan. This is
/// Appendix A #15: a `filesystem list` right after a `filesystem trash` still returns the trashed
/// node about two thirds of the time, and believing it made deleted files resurrect.</item>
/// <item><b>Writes</b> (<see cref="SuppressWrite"/> → <see cref="IsEcho"/>): a scan that reports a
/// file we just downloaded is <i>right</i> — the file really is there — so it must NOT be filtered
/// out. What has to be ignored is the *watcher event* the write generated. §9 calls this "the
/// classic bug for this feature": unsuppressed, every download looks like a local change, which
/// syncs, which writes, forever.</item>
/// </list>
///
/// Filtering writes out of a scan the way deletions are would be actively harmful: the reconciler
/// would see the just-downloaded file as absent locally and download it again, or decide the user
/// had deleted it.
///
/// In-memory on purpose: the window is seconds, and a process restart takes longer than the
/// staleness it guards against, so there is nothing worth persisting.
/// </summary>
public sealed class SyncEchoSuppressor
{
    /// <summary>
    /// Measured convergence for a remote trash was ~7s (Appendix A #15); 60s is generous enough
    /// to cover a bad day without being long enough to matter to a user who genuinely re-creates
    /// the same path — and even then, <see cref="Filter"/> releases the entry as soon as a scan
    /// confirms the deletion landed.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;
    private readonly Dictionary<Key, DateTimeOffset> _deletedUntil = new();
    private readonly Dictionary<Key, DateTimeOffset> _writtenUntil = new();
    private readonly object _gate = new();

    public SyncEchoSuppressor(TimeProvider? timeProvider = null, TimeSpan? window = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    private readonly record struct Key(int PairId, SyncSide Side, string RelativePath);

    /// <summary>Call right after this engine deletes (or trashes) a node on <paramref name="side"/>.</summary>
    public void SuppressDeletion(int pairId, SyncSide side, string relativePath)
        => Remember(_deletedUntil, new Key(pairId, side, relativePath));

    /// <summary>
    /// Call right after this engine writes a node (download, folder creation, conflict rename).
    /// Only affects <see cref="IsEcho"/> — never scan filtering.
    /// </summary>
    public void SuppressWrite(int pairId, SyncSide side, string relativePath)
        => Remember(_writtenUntil, new Key(pairId, side, relativePath));

    /// <summary>
    /// Whether an observed *event* for this path is just our own action coming back — what
    /// <see cref="LocalFileWatcher"/> asks before waking the scheduler. Covers both registers: our
    /// writes and our deletions both generate watcher events.
    /// </summary>
    public bool IsEcho(int pairId, SyncSide side, string relativePath)
    {
        lock (_gate)
        {
            Prune();
            return IsActive(_writtenUntil, pairId, side, relativePath)
                   || IsActive(_deletedUntil, pairId, side, relativePath);
        }
    }

    /// <summary>
    /// Drops entries a recent deletion should have removed. Also <b>releases</b> suppression for
    /// paths the scan agrees are gone: once the two sides tell the same story there's nothing left
    /// to suppress, which keeps the window as short as the facts allow rather than always 60s.
    /// </summary>
    public IReadOnlyDictionary<string, NodeFingerprint> Filter(
        int pairId, SyncSide side, IReadOnlyDictionary<string, NodeFingerprint> scanned)
    {
        List<string> active;
        lock (_gate)
        {
            Prune();
            active = _deletedUntil.Keys
                .Where(k => k.PairId == pairId && k.Side == side)
                .Select(k => k.RelativePath)
                .ToList();

            // Release the ones the scan already agrees are gone.
            foreach (var path in active.Where(p => !IsCoveredBy(p, scanned)).ToList())
            {
                _deletedUntil.Remove(new Key(pairId, side, path));
                active.Remove(path);
            }
        }

        if (active.Count == 0)
        {
            return scanned;
        }

        var filtered = new Dictionary<string, NodeFingerprint>(StringComparer.Ordinal);
        foreach (var (path, fingerprint) in scanned)
        {
            if (!active.Any(suppressed => IsSelfOrDescendant(path, suppressed)))
            {
                filtered[path] = fingerprint;
            }
        }

        return filtered;
    }

    private void Remember(Dictionary<Key, DateTimeOffset> register, Key key)
    {
        lock (_gate)
        {
            register[key] = _timeProvider.GetUtcNow() + _window;
        }
    }

    /// <summary>Caller must hold the lock.</summary>
    private void Prune()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var register in (Dictionary<Key, DateTimeOffset>[])[_deletedUntil, _writtenUntil])
        {
            foreach (var key in register.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            {
                register.Remove(key);
            }
        }
    }

    private static bool IsActive(Dictionary<Key, DateTimeOffset> register, int pairId, SyncSide side, string relativePath)
        => register.Keys.Any(k => k.PairId == pairId && k.Side == side && IsSelfOrDescendant(relativePath, k.RelativePath));

    private static bool IsCoveredBy(string suppressedPath, IReadOnlyDictionary<string, NodeFingerprint> scanned)
        => scanned.Keys.Any(path => IsSelfOrDescendant(path, suppressedPath));

    /// <summary>
    /// Deleting a folder takes its whole subtree with it, so a stale listing can report the folder
    /// *and* its children. Suppressing only the exact path would let the children through as
    /// "new remotely" — the same resurrection bug one level down. The same reasoning applies to a
    /// folder we created locally: the events for its contents are ours too.
    /// </summary>
    private static bool IsSelfOrDescendant(string path, string ancestorOrSelf)
        => string.Equals(path, ancestorOrSelf, StringComparison.Ordinal)
           || path.StartsWith(ancestorOrSelf + '/', StringComparison.Ordinal);
}
