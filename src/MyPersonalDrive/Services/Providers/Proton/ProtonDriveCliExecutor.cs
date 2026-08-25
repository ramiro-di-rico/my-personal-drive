using MyPersonalDrive.Services;
using System.Diagnostics;
using System.Text;

namespace MyPersonalDrive.Services.Providers.Proton;

public sealed class ProtonDriveCliExecutor : IProtonDriveCliExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IProtonDriveCliLocator _locator;

    /// <summary>
    /// docs/PLAN-LOCAL-SYNC.md §9's global gate over `proton-drive` processes, now a
    /// reader/writer gate rather than a plain mutex. <b>Still a correctness requirement, not a
    /// throttle.</b> Appendix A #11 measured that concurrent CLI processes crash each other on the
    /// CLI's own internal SQLite cache (`SQLITE_BUSY`) — re-measured against cli-drive@0.6.0 at
    /// 15 failures in 64 calls with eight in flight, so the hazard is unchanged in the current CLI.
    ///
    /// What *did* change is the reason for it: the contention is entirely over one shared cache
    /// file, and giving each concurrent process its own <c>XDG_CACHE_HOME</c> removes it — 64 of 64
    /// calls clean under the same eight-way load (Appendix A #16). So read-only commands now run
    /// <see cref="_readSlots"/>-wide, one private cache each, while everything else keeps the old
    /// exclusive behaviour.
    ///
    /// Permits are held per *invocation* (~3.5s), never per sync cycle, so an interactive click
    /// waits behind at most one command instead of a whole scan.
    /// </summary>
    private readonly SemaphoreSlim _slots;

    /// <summary>
    /// Taken briefly by readers and held throughout by writers. Without it a writer draining
    /// <see cref="_slots"/> one permit at a time could be starved indefinitely by a stream of
    /// readers; with it, readers queue behind a waiting writer. Only one writer drains at a time,
    /// which is also what keeps two writers from deadlocking half-drained.
    /// </summary>
    private readonly SemaphoreSlim _writerGate = new(1, 1);

    private readonly int _readSlots;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _cacheResetGate = new(1, 1);
    private readonly int[] _slotInUse;

    /// <summary>0/1 rather than a bool so it can be read and written through <see cref="Volatile"/>.</summary>
    private int _readCachesDirty;

    /// <param name="maxReadConcurrency">
    /// How many read-only CLI processes may run at once. Zero derives it from the CPU count, which
    /// is the right basis: the ~3.5s per call is almost all Node.js startup (Appendix A #11a), so
    /// the ceiling is cores, not network. Capped at 8 — measured throughput flattens there.
    /// </param>
    /// <param name="cacheRoot">
    /// Where the per-slot CLI caches live. Defaults under the app's own cache directory, never the
    /// CLI's own <c>~/.cache/proton-drive-cli</c>: the whole point is that these are disposable and
    /// app-owned, and wiping the user's real CLI cache would be someone else's state to destroy.
    /// </param>
    public ProtonDriveCliExecutor(IProtonDriveCliLocator locator, int maxReadConcurrency = 0, string? cacheRoot = null)
    {
        _locator = locator;
        _readSlots = maxReadConcurrency > 0
            ? maxReadConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 8);
        _slots = new SemaphoreSlim(_readSlots, _readSlots);
        _slotInUse = new int[_readSlots];
        _cacheRoot = cacheRoot ?? Path.Combine(DefaultCacheHome(), "MyPersonalDrive", "cli-cache");
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        return IsReadOnly(arguments)
            ? await ExecuteAsReaderAsync(arguments, cancellationToken, timeout)
            : await ExecuteAsWriterAsync(arguments, cancellationToken, timeout);
    }

    /// <summary>
    /// Which commands may run alongside each other. Deliberately a short allow-list rather than a
    /// deny-list of mutations: an unrecognised command — a new one, or a typo — must fall through to
    /// the exclusive path, because being wrong in that direction only costs time, while being wrong
    /// the other way corrupts.
    /// </summary>
    private static bool IsReadOnly(IReadOnlyList<string> arguments)
        => arguments.Count >= 2
            && arguments[0] == "filesystem"
            && arguments[1] is "list" or "info";

    private async Task<string> ExecuteAsReaderAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        // Touch the writer gate without holding it: this is what stops a steady stream of readers
        // from starving a writer that is part-way through draining the slots.
        await _writerGate.WaitAsync(cancellationToken);
        _writerGate.Release();

        await _slots.WaitAsync(cancellationToken);
        var slot = TakeSlot();
        try
        {
            await DiscardReadCachesIfDirtyAsync(cancellationToken);
            return await ExecuteInSlotAsync(arguments, slot, cancellationToken, timeout);
        }
        finally
        {
            ReturnSlot(slot);
            _slots.Release();
        }
    }

    private async Task<string> ExecuteAsWriterAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        await _writerGate.WaitAsync(cancellationToken);
        var acquired = 0;
        try
        {
            for (; acquired < _readSlots; acquired++)
            {
                await _slots.WaitAsync(cancellationToken);
            }

            // Writers always use slot 0's cache, so a run of mutations builds on one coherent view
            // instead of scattering half-updated caches across slots.
            return await ExecuteInSlotAsync(arguments, 0, cancellationToken, timeout);
        }
        finally
        {
            // The mutation may have landed even if this threw, so mark regardless: a read slot that
            // never saw the write would otherwise answer from a cache that predates it.
            Volatile.Write(ref _readCachesDirty, 1);
            if (acquired > 0)
            {
                _slots.Release(acquired);
            }

            _writerGate.Release();
        }
    }

    public async Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default)
    {
        // Exclusive: wiping a cache directory out from under a running process is the SQLITE_BUSY
        // failure by another route.
        await _writerGate.WaitAsync(cancellationToken);
        var acquired = 0;
        try
        {
            for (; acquired < _readSlots; acquired++)
            {
                await _slots.WaitAsync(cancellationToken);
            }

            await DiscardAllCachesAsync();
            Volatile.Write(ref _readCachesDirty, 0);
        }
        finally
        {
            if (acquired > 0)
            {
                _slots.Release(acquired);
            }

            _writerGate.Release();
        }
    }

    private async Task DiscardReadCachesIfDirtyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _readCachesDirty) == 0)
        {
            return;
        }

        // Only one reader does the work; the rest of the wave sees a clean flag and moves on. This
        // fires once per write→read transition, not once per action, so a long run of uploads costs
        // one discard rather than one each.
        await _cacheResetGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _readCachesDirty) == 0)
            {
                return;
            }

            await DiscardAllCachesAsync();
            Volatile.Write(ref _readCachesDirty, 0);
        }
        finally
        {
            _cacheResetGate.Release();
        }
    }

    /// <summary>
    /// Off the calling thread: recursively deleting the slot directories is synchronous file I/O, and
    /// the browse path reaches this from the UI thread, where a slow disk would show up as the window
    /// freezing. Same reason the SQLite stores hop off-thread — see <see cref="SqliteOffThread"/>.
    /// </summary>
    private Task DiscardAllCachesAsync() => Task.Run(DiscardAllCaches);

    private void DiscardAllCaches()
    {
        for (var slot = 0; slot < _readSlots; slot++)
        {
            var path = SlotCachePath(slot);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A cache we can't clear is a slow next scan, never a wrong one — the CLI rebuilds
                // whatever it can't read. Failing the sync over it would be the worse trade.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string SlotCachePath(int slot) => Path.Combine(_cacheRoot, $"slot-{slot}");

    /// <summary>
    /// The XDG cache location, not the data one: every byte under here is disposable by design —
    /// <see cref="ResetRemoteCacheAsync"/> deletes it outright — and losing it costs one cold scan.
    /// </summary>
    private static string DefaultCacheHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return xdg;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "mypersonaldrive-cache")
            : Path.Combine(home, ".cache");
    }

    private int TakeSlot()
    {
        // A permit was already taken, so a free id is guaranteed to be there.
        for (var slot = 0; slot < _readSlots; slot++)
        {
            if (Interlocked.CompareExchange(ref _slotInUse[slot], 1, 0) == 0)
            {
                return slot;
            }
        }

        throw new InvalidOperationException("No free CLI slot despite holding a permit.");
    }

    private void ReturnSlot(int slot) => Volatile.Write(ref _slotInUse[slot], 0);

    private async Task<string> ExecuteInSlotAsync(IReadOnlyList<string> arguments, int slot, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var fileName = _locator.Locate();
        var commandText = FormatCommandText(fileName, arguments);
        var cachePath = SlotCachePath(slot);
        Directory.CreateDirectory(cachePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // The whole basis of running these in parallel. The CLI keeps its SQLite cache under
        // XDG_CACHE_HOME, and it is that one shared file the concurrent processes deadlock on, so a
        // private directory per slot is what makes the concurrency safe rather than lucky. Set on
        // every invocation, writers included, so no command can fall back to the CLI's own cache.
        startInfo.Environment["XDG_CACHE_HOME"] = cachePath;

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var timeoutCts = CreateTimeoutCts(timeout);
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var effectiveToken = linkedCts?.Token ?? cancellationToken;

        using var cancellationRegistration = effectiveToken.Register(() =>
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (PlatformNotSupportedException)
                {
                }
            }
        });

        CommandStarted?.Invoke(this, new CliCommandStartedEventArgs(commandText));

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Proton Drive CLI.");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
            lock (stdout)
            {
                stdout.AppendLine(line);
            }

            CommandOutput?.Invoke(this, new CliCommandOutputEventArgs(line, isError: false));
        }, effectiveToken);

        var stderrTask = ReadStreamAsync(process.StandardError, line =>
        {
            lock (stderr)
            {
                stderr.AppendLine(line);
            }

            CommandOutput?.Invoke(this, new CliCommandOutputEventArgs(line, isError: true));
        }, effectiveToken);

        try
        {
            var exitTask = process.WaitForExitAsync(effectiveToken);
            await Task.WhenAll(stdoutTask, stderrTask, exitTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
        {
            CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, -1));
            throw new CliException(commandText, -1, stdout.ToString(), stderr.ToString(),
                $"Command timed out after {timeout ?? DefaultTimeout}.", CliErrorKind.Timeout);
        }

        if (process.ExitCode != 0)
        {
            var stdoutText = stdout.ToString();
            var stderrText = stderr.ToString();
            // An internal CLI crash puts a bare `====` banner on stderr and the real diagnosis on
            // stdout, so "stderr if non-empty" produced exceptions whose entire message was
            // `===============`. Fall through to stdout whenever stderr carries no actual words.
            var errorText = HasContent(stderrText) ? stderrText : stdoutText;
            var kind = CliErrorClassifier.Classify(process.ExitCode, stdoutText, stderrText);
            CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
            throw new CliException(
                commandText,
                process.ExitCode,
                stdoutText,
                stderrText,
                string.IsNullOrWhiteSpace(errorText) ? $"Command failed with exit code {process.ExitCode}." : errorText,
                kind);
        }

        CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
        return stdout.ToString();
    }

    private static CancellationTokenSource? CreateTimeoutCts(TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(effectiveTimeout);
        return cts;
    }

    private static string FormatCommandText(string fileName, IReadOnlyList<string> arguments)
    {
        // Presentation only (shown in the activity console); never fed back into execution.
        var quoted = arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
        return $"{fileName} {string.Join(' ', quoted)}".Trim();
    }

    /// <summary>
    /// Whether a captured stream holds anything a human could act on. The CLI's crash banner is
    /// pure `=` and whitespace, which is non-empty but says nothing.
    /// </summary>
    private static bool HasContent(string text)
        => text.AsSpan().TrimStart().TrimStart('=').TrimStart().Length > 0;

    private static async Task ReadStreamAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }
}
