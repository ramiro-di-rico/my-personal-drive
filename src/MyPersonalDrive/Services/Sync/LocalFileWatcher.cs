using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// Watches one pair's local folder and reports *settled* changes — docs/PLAN-LOCAL-SYNC.md §6.3.
/// A thin adapter: the debounce lives in <see cref="ChangeDebouncer"/> and the decision to sync
/// lives in <see cref="SyncScheduler"/>. Per §6.3 this class never starts a sync itself; it only
/// marks the pair dirty, which is what keeps "something changed" separable from "now is a good
/// time to act on it".
/// </summary>
public sealed class LocalFileWatcher : IDisposable
{
    /// <summary>
    /// §6.3's raised buffer. The default (8 KB) overflows easily — extracting an archive into a
    /// watched folder can produce thousands of events faster than they're drained.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    private readonly SyncPair _pair;
    private readonly PathMapper _mapper;
    private readonly ExclusionMatcher _exclusions;
    private readonly SyncEchoSuppressor _echoSuppressor;
    private readonly ChangeDebouncer _debouncer;
    private FileSystemWatcher? _watcher;

    public LocalFileWatcher(
        SyncPair pair,
        SyncEchoSuppressor echoSuppressor,
        ChangeDebouncer? debouncer = null,
        TimeProvider? timeProvider = null)
    {
        _pair = pair;
        _mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        _exclusions = new ExclusionMatcher(pair.ExcludeGlobs);
        _echoSuppressor = echoSuppressor;
        _debouncer = debouncer ?? new ChangeDebouncer(timeProvider);
    }

    private volatile bool _needsFullScan;

    /// <summary>
    /// Set when the OS told us it dropped events, so the next cycle cannot trust incremental
    /// information and must do a full scan instead (§6.3). The scheduler clears it once it has acted
    /// on it — a latch that never reset would mean one overflow marked every later batch forever.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> because the two ends are different threads: the OS raises
    /// <see cref="FileSystemWatcher.Error"/> on a threadpool thread while the scheduler's loop reads
    /// this. Without it the loop could keep reading a stale <c>false</c> and never learn events were
    /// dropped.
    /// </remarks>
    public bool NeedsFullScan
    {
        get => _needsFullScan;
        set => _needsFullScan = value;
    }

    /// <summary>
    /// True when this pair could not be watched at all and must fall back to periodic scanning —
    /// on Linux, typically `fs.inotify.max_user_watches` exhaustion (§6.3).
    /// </summary>
    public bool IsDegraded { get; private set; }

    public string? DegradedReason { get; private set; }

    /// <summary>Raised once per batch of settled paths. Fires on a threadpool thread.</summary>
    public event EventHandler<IReadOnlyList<string>>? ChangesSettled;

    public void Start()
    {
        try
        {
            _watcher = new FileSystemWatcher(_pair.LocalPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The inotify watch limit surfaces here as an IOException. Degrading to periodic
            // scanning is strictly better than refusing to sync the pair at all; the UI is
            // expected to surface DegradedReason, including the sysctl to raise the limit.
            IsDegraded = true;
            DegradedReason = OperatingSystem.IsLinux()
                ? $"No se pudieron vigilar los cambios de '{_pair.LocalPath}' ({ex.Message}). Se pasa a " +
                  "análisis periódico. Si es el límite de watches de inotify, subilo con: " +
                  "sudo sysctl fs.inotify.max_user_watches=524288"
                : $"No se pudieron vigilar los cambios de '{_pair.LocalPath}' ({ex.Message}). Se pasa a análisis periódico.";
            _watcher = null;
        }
    }

    /// <summary>
    /// Moves everything that has gone quiet out of the debouncer and raises
    /// <see cref="ChangesSettled"/> if anything survived filtering. Driven by the scheduler's tick
    /// rather than an internal timer, so one loop owns all the periodic work.
    /// </summary>
    public void Pump()
    {
        var settled = _debouncer.TakeSettled();
        if (settled.Count > 0)
        {
            ChangesSettled?.Invoke(this, settled);
        }
    }

    public bool HasPendingChanges => _debouncer.HasPending;

    private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both ends matter: the old path disappeared and the new one appeared.
        Record(e.OldFullPath);
        Record(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow: events were dropped, so what's in the debouncer is an incomplete
        // picture. Flag a full scan and throw away the partial batch rather than acting on it.
        NeedsFullScan = true;
        _debouncer.Clear();
    }

    private void Record(string fullPath)
    {
        string relativePath;
        try
        {
            relativePath = _mapper.ToRelativeFromLocal(fullPath);
        }
        catch (ArgumentException)
        {
            return; // outside the pair's root — not ours
        }

        if (relativePath.Length == 0)
        {
            return; // the root folder itself
        }

        // Excluded paths (.git/, *.tmp, our own trash and temp folders) must never wake a sync.
        if (_exclusions.IsExcluded(relativePath, Directory.Exists(fullPath)))
        {
            return;
        }

        // §9's classic bug: the executor's own writes come back as events. Without this, every
        // download would look like a local change, which would sync, which would write, forever.
        if (_echoSuppressor.IsEcho(_pair.Id, SyncSide.Local, relativePath))
        {
            return;
        }

        _debouncer.Record(relativePath);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
