using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Remembers, for a short window, nodes this engine just deleted, so a scan that still reports
/// them as present doesn't get believed. One mechanism for both halves of
/// docs/PLAN-LOCAL-SYNC.md's echo problem:
///
/// <list type="bullet">
/// <item><b>Remote (live now, Appendix A #15)</b>: a `filesystem list` issued right after a
/// `filesystem trash` still returns the trashed node about two thirds of the time. Left
/// unfiltered, the next run reconciles `L=absent, R=present, B=absent`, reads it as "new
/// remotely" per §5.2, and <i>re-downloads the file the user just deleted</i>.</item>
/// <item><b>Local (§9, for F3)</b>: the same shape appears when the `FileSystemWatcher` reports
/// the engine's own writes back to it. That's why this takes a <see cref="SyncSide"/> rather than
/// being remote-only — §9 calls the watcher version "the classic bug for this feature".</item>
/// </list>
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
    private readonly Dictionary<(int PairId, SyncSide Side, string RelativePath), DateTimeOffset> _suppressedUntil = new();
    private readonly object _gate = new();

    public SyncEchoSuppressor(TimeProvider? timeProvider = null, TimeSpan? window = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Call right after this engine deletes (or trashes) a node on <paramref name="side"/>.</summary>
    public void SuppressDeletion(int pairId, SyncSide side, string relativePath)
    {
        lock (_gate)
        {
            _suppressedUntil[(pairId, side, relativePath)] = _timeProvider.GetUtcNow() + _window;
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
            var now = _timeProvider.GetUtcNow();
            foreach (var key in _suppressedUntil.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            {
                _suppressedUntil.Remove(key);
            }

            active = _suppressedUntil.Keys
                .Where(k => k.PairId == pairId && k.Side == side)
                .Select(k => k.RelativePath)
                .ToList();

            // Release the ones the scan already agrees are gone.
            foreach (var path in active.Where(p => !IsCoveredBy(p, scanned)).ToList())
            {
                _suppressedUntil.Remove((pairId, side, path));
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

    private static bool IsCoveredBy(string suppressedPath, IReadOnlyDictionary<string, NodeFingerprint> scanned)
        => scanned.Keys.Any(path => IsSelfOrDescendant(path, suppressedPath));

    /// <summary>
    /// Deleting a folder takes its whole subtree with it, so a stale listing can report the folder
    /// *and* its children. Suppressing only the exact path would let the children through as
    /// "new remotely" — the same resurrection bug one level down.
    /// </summary>
    private static bool IsSelfOrDescendant(string path, string ancestorOrSelf)
        => string.Equals(path, ancestorOrSelf, StringComparison.Ordinal)
           || path.StartsWith(ancestorOrSelf + '/', StringComparison.Ordinal);
}
