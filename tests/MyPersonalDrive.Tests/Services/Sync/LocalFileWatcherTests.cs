using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// Against a real temp directory and a real <c>FileSystemWatcher</c> — the debounce logic itself is
/// covered without IO in <see cref="ChangeDebouncerTests"/>, so what's left to prove here is the
/// adapter: that real events are mapped to relative paths, that exclusions and echoes are dropped,
/// and above all that the engine's own writes never come back as changes.
/// </summary>
public class LocalFileWatcherTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-watcher-tests").FullName;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00Z");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_localRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SyncPair Pair() => new(
        Id: 1, RemotePath: "/my-files/Docs", LocalPath: _localRoot,
        Direction: SyncDirection.TwoWay, ConflictPolicy: ConflictPolicy.Ask,
        IsEnabled: true, IsPaused: false, ExcludeGlobs: [],
        LastSyncAt: null, LastStatus: SyncPairStatus.Never, LastError: null);

    /// <summary>
    /// Waits for the watcher to have seen something, then releases the debounce by advancing the
    /// fake clock. Real filesystem events are asynchronous, so the *arrival* has to be waited for;
    /// the quiet period itself is fake-clock driven, so no test ever sleeps 2 real seconds.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CollectAsync(
        LocalFileWatcher watcher, FakeTimeProvider clock, Action act, int settleMs = 1500)
    {
        var collected = new List<string>();
        watcher.ChangesSettled += (_, paths) => collected.AddRange(paths);

        act();

        var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
        while (DateTime.UtcNow < deadline && !watcher.HasPendingChanges)
        {
            await Task.Delay(25);
        }

        clock.Advance(ChangeDebouncer.DefaultQuietPeriod);
        watcher.Pump();
        return collected;
    }

    [Fact]
    public async Task AUserEdit_IsReportedAsARelativePath()
    {
        var clock = new FakeTimeProvider(T0);
        using var watcher = new LocalFileWatcher(Pair(), new SyncEchoSuppressor(clock), timeProvider: clock);
        watcher.Start();
        Assert.False(watcher.IsDegraded);

        var collected = await CollectAsync(watcher, clock,
            () => File.WriteAllText(Path.Combine(_localRoot, "notes.txt"), "typed by the user"));

        Assert.Contains("notes.txt", collected);
    }

    [Fact]
    public async Task TheEnginesOwnWrite_NeverComesBackAsAChange()
    {
        // docs/PLAN-LOCAL-SYNC.md §14's high-impact risk and §9's "classic bug for this feature":
        // if a download registers as a local change, it syncs, which writes, which syncs, forever.
        var clock = new FakeTimeProvider(T0);
        var suppressor = new SyncEchoSuppressor(clock);
        using var watcher = new LocalFileWatcher(Pair(), suppressor, timeProvider: clock);
        watcher.Start();

        var collected = await CollectAsync(watcher, clock, () =>
        {
            // Exactly what SyncExecutor.DownloadFileAsync does: register, then write.
            suppressor.SuppressWrite(1, SyncSide.Local, "downloaded.txt");
            File.WriteAllText(Path.Combine(_localRoot, "downloaded.txt"), "came from the cloud");
        });

        Assert.DoesNotContain("downloaded.txt", collected);
    }

    [Fact]
    public async Task AWriteInsideAFolderTheEngineJustCreated_IsAlsoAnEcho()
    {
        var clock = new FakeTimeProvider(T0);
        var suppressor = new SyncEchoSuppressor(clock);
        using var watcher = new LocalFileWatcher(Pair(), suppressor, timeProvider: clock);
        watcher.Start();

        var collected = await CollectAsync(watcher, clock, () =>
        {
            suppressor.SuppressWrite(1, SyncSide.Local, "Photos");
            Directory.CreateDirectory(Path.Combine(_localRoot, "Photos"));
            File.WriteAllText(Path.Combine(_localRoot, "Photos", "pic.jpg"), "downloaded content");
        });

        Assert.Empty(collected);
    }

    [Fact]
    public async Task ExcludedPaths_NeverWakeASync()
    {
        var clock = new FakeTimeProvider(T0);
        using var watcher = new LocalFileWatcher(Pair(), new SyncEchoSuppressor(clock), timeProvider: clock);
        watcher.Start();

        var collected = await CollectAsync(watcher, clock, () =>
        {
            Directory.CreateDirectory(Path.Combine(_localRoot, ".git"));
            File.WriteAllText(Path.Combine(_localRoot, ".git", "HEAD"), "ref: refs/heads/main");
            File.WriteAllText(Path.Combine(_localRoot, "scratch.tmp"), "transient");

            // Our own trash folder is excluded too — a DeleteLocal moves files in there, and that
            // must not read as the user creating files.
            Directory.CreateDirectory(Path.Combine(_localRoot, ".mypersonaldrive-trash"));
            File.WriteAllText(Path.Combine(_localRoot, ".mypersonaldrive-trash", "gone.txt"), "trashed");
        });

        Assert.Empty(collected);
    }

    [Fact]
    public async Task ARename_ReportsBothEnds()
    {
        var clock = new FakeTimeProvider(T0);
        File.WriteAllText(Path.Combine(_localRoot, "before.txt"), "content");

        using var watcher = new LocalFileWatcher(Pair(), new SyncEchoSuppressor(clock), timeProvider: clock);
        watcher.Start();

        var collected = await CollectAsync(watcher, clock,
            () => File.Move(Path.Combine(_localRoot, "before.txt"), Path.Combine(_localRoot, "after.txt")));

        Assert.Contains("before.txt", collected); // it disappeared
        Assert.Contains("after.txt", collected);  // and it appeared
    }

    [Fact]
    public void AnUnwatchableFolder_DegradesToPollingInsteadOfFailing()
    {
        var clock = new FakeTimeProvider(T0);
        var pair = Pair() with { LocalPath = Path.Combine(_localRoot, "does-not-exist") };
        using var watcher = new LocalFileWatcher(pair, new SyncEchoSuppressor(clock), timeProvider: clock);

        watcher.Start();

        Assert.True(watcher.IsDegraded);
        Assert.NotNull(watcher.DegradedReason);
        if (OperatingSystem.IsLinux())
        {
            // The message has to tell the user what to do about the inotify limit (§6.3).
            Assert.Contains("max_user_watches", watcher.DegradedReason);
        }
    }
}
