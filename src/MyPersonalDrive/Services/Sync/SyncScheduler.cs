using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Drives automatic synchronization — docs/PLAN-LOCAL-SYNC.md §6.4. Owns one
/// <see cref="LocalFileWatcher"/> per pair, decides when each pair is due via
/// <see cref="SyncSchedulePolicy"/>, and runs the cycles.
///
/// <b>Cycles run strictly one at a time, across all pairs.</b> Not for tidiness: Appendix A #11
/// found that concurrent `proton-drive` processes crash each other on the CLI's internal SQLite
/// cache, so two pairs syncing at once is a correctness problem. The per-pair lock the plan asks
/// for is therefore subsumed by a single global one.
/// </summary>
public sealed class SyncScheduler : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PairRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly SyncStateStore _stateStore;
    private readonly SyncExecutor _executor;
    private readonly SyncEchoSuppressor _echoSuppressor;
    private readonly Func<bool> _isAuthenticated;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tick;

    private readonly Dictionary<int, PairRuntime> _pairs = [];

    /// <summary>
    /// Guards <see cref="_pairs"/>. The loop is the only writer, but <see cref="PumpOnceAsync"/> and
    /// <see cref="StopAsync"/> are public, so nothing stops a second caller enumerating the
    /// dictionary while the loop is adding to it. Never held across an <c>await</c> — every use takes
    /// a snapshot and then works outside the lock.
    /// </summary>
    private readonly object _pairsGate = new();
    private readonly SemaphoreSlim _oneCycleAtATime = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _pairsLoadedAt = DateTimeOffset.MinValue;

    public SyncScheduler(
        SyncStateStore stateStore,
        SyncExecutor executor,
        SyncEchoSuppressor echoSuppressor,
        Func<bool> isAuthenticated,
        TimeProvider? timeProvider = null,
        TimeSpan? tick = null)
    {
        _stateStore = stateStore;
        _executor = executor;
        _echoSuppressor = echoSuppressor;
        _isAuthenticated = isAuthenticated;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tick = tick ?? DefaultTick;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Whether this account has a session at all. The loop is started for every configured
    /// provider and gates each cycle on this (see <c>RunAsync</c>), so "running" on its own says
    /// nothing about whether anything can actually sync — which is why the panel showed five
    /// accounts as "activada" when only one was signed in (docs/PLAN-UX-ROUND-2.md §11).
    /// </summary>
    public bool IsAccountAuthenticated => _isAuthenticated();

    /// <summary>Raised after every automatic cycle, for the UI to refresh a row.</summary>
    public event EventHandler<int>? PairSynced;

    /// <summary>Raised when a pair could not be watched and fell back to polling (§6.3).</summary>
    public event EventHandler<string>? WatcherDegraded;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        // Task.Run, not a bare call: `Start` is invoked from the UI thread, so a bare call would
        // capture Avalonia's synchronization context and post every continuation in the loop back to
        // the UI thread. That deadlocked shutdown outright — `ShutdownRequested` blocks the UI thread
        // waiting on this task (App.axaml.cs), while the task needs that same thread to advance. It
        // also meant every await point in a sync cycle competed with rendering for no reason.
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                await _loop;
            }
        }
        catch (OperationCanceledException)
        {
        }

        List<PairRuntime> toDispose;
        lock (_pairsGate)
        {
            toDispose = [.. _pairs.Values];
            _pairs.Clear();
        }

        foreach (var runtime in toDispose)
        {
            runtime.Watcher?.Dispose();
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>
    /// Runs one due cycle immediately if anything is due, and reports whether it did. Exposed for
    /// tests and for a deterministic "tick now" — the loop itself is just this on a timer.
    /// </summary>
    public async Task<bool> PumpOnceAsync(CancellationToken cancellationToken)
    {
        if (!_isAuthenticated())
        {
            // §6.4's global pause. Nothing can succeed without a session, and every attempt would
            // cost a ~3.5s process to fail.
            return false;
        }

        await RefreshPairsAsync(cancellationToken);

        var snapshot = SnapshotPairs();
        foreach (var runtime in snapshot)
        {
            runtime.Watcher?.Pump();
        }

        var now = _timeProvider.GetUtcNow();
        var due = snapshot
            .Where(runtime => SyncSchedulePolicy.ShouldRunNow(runtime.ToScheduleState(), now))
            .OrderBy(runtime => runtime.Pair.Id)
            .ToList();

        var ranAnything = false;
        foreach (var runtime in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunOneCycleAsync(runtime, cancellationToken);
            ranAnything = true;
        }

        return ranAnything;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PumpOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure — a scheduler that dies silently is
                // worse than one that logs and keeps going.
                await SafeLogAsync(null, SyncLogLevel.Error, $"Scheduler tick failed: {ex.Message}", cancellationToken);
            }

            try
            {
                await Task.Delay(_tick, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOneCycleAsync(PairRuntime runtime, CancellationToken cancellationToken)
    {
        await _oneCycleAtATime.WaitAsync(cancellationToken);
        var startedAt = _timeProvider.GetUtcNow();
        try
        {
            // Consumed before the run, not after: a change arriving *during* the cycle must leave
            // the pair dirty so the next tick picks it up, instead of being cleared by this one.
            runtime.IsDirty = false;
            runtime.NeedsFullScan = false;

            // The watcher's own latch has to be cleared too, not just the runtime's copy. It is set
            // once on buffer overflow and never resets itself, so leaving it would make every later
            // batch re-raise "needs a full scan" for the rest of the process's life.
            if (runtime.Watcher is not null)
            {
                runtime.Watcher.NeedsFullScan = false;
            }

            await _executor.RunAsync(runtime.Pair, cancellationToken);
            runtime.ConsecutiveErrors = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.ConsecutiveErrors++;
            await SafeLogAsync(runtime.Pair.Id, SyncLogLevel.Error,
                $"La sincronización automática falló (intento {runtime.ConsecutiveErrors}, próximo intento en " +
                $"{SyncSchedulePolicy.ErrorBackoff(runtime.ConsecutiveErrors).TotalMinutes:0} min): {ex.Message}",
                cancellationToken);
        }
        finally
        {
            var finishedAt = _timeProvider.GetUtcNow();
            runtime.LastRunAt = finishedAt;
            runtime.LastCycleDuration = finishedAt - startedAt;
            _oneCycleAtATime.Release();
        }

        PairSynced?.Invoke(this, runtime.Pair.Id);
    }

    private List<PairRuntime> SnapshotPairs()
    {
        lock (_pairsGate)
        {
            return [.. _pairs.Values];
        }
    }

    private async Task RefreshPairsAsync(CancellationToken cancellationToken)
    {
        // Purely time-based. It used to also require `_pairs.Count > 0`, which meant that with no
        // pairs configured the guard never held and the loop re-queried the database on every 2s
        // tick — forever, for nothing.
        var now = _timeProvider.GetUtcNow();
        if (_pairsLoadedAt != DateTimeOffset.MinValue && now - _pairsLoadedAt < PairRefreshInterval)
        {
            return;
        }

        _pairsLoadedAt = now;
        var pairs = await _stateStore.GetPairsAsync(cancellationToken);
        var enabled = pairs.Where(p => p.IsEnabled).ToList();
        var seen = enabled.Select(p => p.Id).ToHashSet();

        foreach (var pair in enabled)
        {
            bool alreadyKnown;
            lock (_pairsGate)
            {
                alreadyKnown = _pairs.TryGetValue(pair.Id, out var existing);
                if (alreadyKnown)
                {
                    existing!.Pair = pair; // pick up IsPaused / exclusion changes
                }
            }

            if (alreadyKnown)
            {
                continue;
            }

            // Built outside the lock: starting a watcher touches the filesystem, and the degraded
            // path logs, which awaits.
            var runtime = new PairRuntime(pair);
            var watcher = new LocalFileWatcher(pair, _echoSuppressor, timeProvider: _timeProvider);
            watcher.ChangesSettled += (_, paths) =>
            {
                runtime.IsDirty = true;
                if (watcher.NeedsFullScan)
                {
                    runtime.NeedsFullScan = true;
                }

                _ = paths; // the paths themselves aren't needed: a cycle rescans the pair anyway
            };

            watcher.Start();
            runtime.Watcher = watcher;

            lock (_pairsGate)
            {
                _pairs[pair.Id] = runtime;
            }

            if (watcher.IsDegraded)
            {
                WatcherDegraded?.Invoke(this, watcher.DegradedReason!);
                await SafeLogAsync(pair.Id, SyncLogLevel.Warning, watcher.DegradedReason!, cancellationToken);
            }
        }

        // Drop pairs the user removed or disabled while we were running. Removed from the dictionary
        // under the lock, disposed outside it.
        List<PairRuntime> dropped;
        lock (_pairsGate)
        {
            var staleIds = _pairs.Keys.Where(id => !seen.Contains(id)).ToList();
            dropped = staleIds.Select(id => _pairs[id]).ToList();
            foreach (var id in staleIds)
            {
                _pairs.Remove(id);
            }
        }

        foreach (var runtime in dropped)
        {
            runtime.Watcher?.Dispose();
        }
    }

    private async Task SafeLogAsync(int? pairId, SyncLogLevel level, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.LogAsync(pairId, level, null, message, _timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to log must never be what takes the scheduler down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _oneCycleAtATime.Dispose();
    }

    private sealed class PairRuntime(SyncPair pair)
    {
        // The two flags a watcher callback writes and the loop reads. `volatile` rather than plain
        // fields because those are different threads: the loop could otherwise keep reading a cached
        // `false` and never notice a change the watcher had already reported. Everything else here is
        // touched only by the loop.
        private volatile bool _isDirty;
        private volatile bool _needsFullScan;

        public SyncPair Pair { get; set; } = pair;
        public LocalFileWatcher? Watcher { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public TimeSpan? LastCycleDuration { get; set; }
        public int ConsecutiveErrors { get; set; }

        public bool IsDirty
        {
            get => _isDirty;
            set => _isDirty = value;
        }

        public bool NeedsFullScan
        {
            get => _needsFullScan;
            set => _needsFullScan = value;
        }

        public PairScheduleState ToScheduleState()
            => new(LastRunAt, LastCycleDuration, ConsecutiveErrors, IsDirty, Pair.IsPaused);
    }
}
