namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Collects raw filesystem events and holds each path back until it has been quiet for a while —
/// docs/PLAN-LOCAL-SYNC.md §6.3's mandatory debounce. A single editor save produces 3–6 events
/// (write, truncate, rename-into-place, attribute change), and an unfiltered watcher would kick off
/// a sync per event, each one racing the still-unfinished write.
///
/// Pure and clock-injected: no `FileSystemWatcher`, no timers, no sleeping in tests.
/// <see cref="LocalFileWatcher"/> is the thin adapter that feeds real events in.
/// </summary>
public sealed class ChangeDebouncer
{
    /// <summary>
    /// Matches <c>LocalScanner</c>'s own "modified less than 2s ago, might still be being written"
    /// guard. Anything shorter and the debounce would release paths the scanner then skips, so the
    /// change would be noticed and then dropped.
    /// </summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _quietPeriod;
    private readonly Dictionary<string, DateTimeOffset> _lastEventAt = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ChangeDebouncer(TimeProvider? timeProvider = null, TimeSpan? quietPeriod = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
    }

    public void Record(string relativePath)
    {
        lock (_gate)
        {
            _lastEventAt[relativePath] = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>True if anything is waiting, settled or not — cheap check for an idle loop.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _lastEventAt.Count > 0;
            }
        }
    }

    /// <summary>
    /// Removes and returns the paths that have now been quiet for the whole quiet period. Paths
    /// still receiving events stay behind for a later call — a long file copy therefore yields
    /// nothing until it stops growing.
    /// </summary>
    public IReadOnlyList<string> TakeSettled()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var settled = _lastEventAt
                .Where(entry => now - entry.Value >= _quietPeriod)
                .Select(entry => entry.Key)
                .ToList();

            foreach (var path in settled)
            {
                _lastEventAt.Remove(path);
            }

            return settled;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lastEventAt.Clear();
        }
    }
}
