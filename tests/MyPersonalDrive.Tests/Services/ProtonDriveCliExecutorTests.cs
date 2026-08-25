using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Tests;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Tests the real <see cref="ProtonDriveCliExecutor"/> — the one that actually spawns processes —
/// by pointing its locator at <c>/bin/sh</c> instead of the Proton CLI. <see cref="Fakes.FakeCliExecutor"/>
/// is a different implementation and cannot cover this.
/// </summary>
public class ProtonDriveCliExecutorTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("mypersonaldrive-executor").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedPathLocator(string path) : IProtonDriveCliLocator
    {
        public string Locate() => path;
    }

    /// <summary>
    /// A stand-in CLI that drops the leading `filesystem &lt;verb&gt;` and runs the rest through
    /// <c>sh</c>. Needed because the executor decides whether a command may run concurrently by
    /// reading those two arguments, so a test that wants the read-only path has to actually pass
    /// them — which plain <c>/bin/sh</c> would try to open as a script file.
    /// </summary>
    private string StubCliThatIgnoresTheSubcommand()
    {
        var path = Path.Combine(_scratch, "stub-cli");
        File.WriteAllText(path, "#!/bin/sh\nshift 2\nexec /bin/sh \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [PosixFact]
    public async Task ConcurrentCalls_AreSerialized_SoTwoCliProcessesNeverOverlap()
    {
        // Why this matters (docs/PLAN-LOCAL-SYNC.md §9 / Appendix A #11): concurrent `proton-drive`
        // processes crash each other on the CLI's own SQLite cache about one time in three. Three
        // unrelated parts of the app spawn CLI processes — the scheduler, the sync panel, and the
        // file browser — so the guarantee has to live in the executor they all share.
        //
        // The probe is a `mkdir` lock: mkdir on an existing directory fails, so any overlap makes a
        // command exit non-zero, which the executor surfaces as a DriveException.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"), cacheRoot: _scratch);
        var lockPath = Path.Combine(_scratch, "lock");
        var script = $"mkdir '{lockPath}' && sleep 0.1 && rmdir '{lockPath}'";

        var calls = Enumerable.Range(0, 6)
            .Select(_ => sut.ExecuteAsync(["-c", script]))
            .ToArray();

        // Serialized: every one of them got the lock uncontended. Unserialized, five of the six
        // would have raced the first and thrown.
        await Task.WhenAll(calls);
        Assert.False(Directory.Exists(lockPath));
    }

    [PosixFact]
    public async Task ReadOnlyCommands_RunConcurrently_InsteadOfQueueing()
    {
        // The throughput change: `filesystem list` is what a remote scan is made of, and serializing
        // it costs ~3.5s per folder (Appendix A #11a). Six calls that each sleep 200ms take ~1.2s
        // serialized and ~0.2s in parallel, so the wall clock distinguishes the two unambiguously.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator(StubCliThatIgnoresTheSubcommand()), maxReadConcurrency: 6, cacheRoot: _scratch);
        var started = DateTimeOffset.UtcNow;

        var calls = Enumerable.Range(0, 6)
            .Select(_ => sut.ExecuteAsync(["filesystem", "list", "-c", "sleep 0.2"]))
            .ToArray();
        await Task.WhenAll(calls);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(900));
    }

    [PosixFact]
    public async Task EachConcurrentReader_GetsItsOwnCacheDirectory()
    {
        // The entire basis for allowing concurrency: the CLI's SQLITE_BUSY crashes come from N
        // processes sharing one XDG_CACHE_HOME, and it stays safe only while the directories differ.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator(StubCliThatIgnoresTheSubcommand()), maxReadConcurrency: 4, cacheRoot: _scratch);

        var calls = Enumerable.Range(0, 4)
            .Select(_ => sut.ExecuteAsync(["filesystem", "list", "-c", "sleep 0.2; echo $XDG_CACHE_HOME"]))
            .ToArray();
        var caches = await Task.WhenAll(calls);

        var distinct = caches.Select(c => c.Trim()).ToHashSet();
        Assert.Equal(4, distinct.Count);
        Assert.All(distinct, c => Assert.StartsWith(_scratch, c));
    }

    [PosixFact]
    public async Task AMutation_StillExcludesEveryOtherCommand()
    {
        // Reads got to overlap; writes did not. A mutation racing a read is the same shared-cache
        // hazard, and `IsReadOnly`'s allow-list is what routes anything unrecognised to this path.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator(StubCliThatIgnoresTheSubcommand()), maxReadConcurrency: 4, cacheRoot: _scratch);
        var lockPath = Path.Combine(_scratch, "writing");

        // The writer raises a marker for as long as it runs; a second writer's mkdir would fail on
        // it, and a reader that sees it was running alongside a mutation. Readers deliberately do
        // *not* take the lock — overlapping each other is the whole point of the read path.
        var mutate = $"mkdir '{lockPath}' && sleep 0.1 && rmdir '{lockPath}'";
        var read = $"sleep 0.05; test ! -d '{lockPath}'";

        var calls = Enumerable.Range(0, 8)
            .Select(i => i % 2 == 0
                ? sut.ExecuteAsync(["filesystem", "trash", "-c", mutate])
                : sut.ExecuteAsync(["filesystem", "list", "-c", read]))
            .ToArray();

        await Task.WhenAll(calls);
        Assert.False(Directory.Exists(lockPath));
    }

    [PosixFact]
    public async Task ResettingTheRemoteCache_DiscardsWhatTheCliCached()
    {
        // A scan that starts from a warm cache can silently omit real nodes (Appendix A #16), so the
        // reset has to actually remove the directory, not merely be called.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator(StubCliThatIgnoresTheSubcommand()), maxReadConcurrency: 2, cacheRoot: _scratch);
        await sut.ExecuteAsync(["filesystem", "list", "-c", "touch \"$XDG_CACHE_HOME/cached.sqlite\""]);
        var cached = Directory.GetFiles(_scratch, "cached.sqlite", SearchOption.AllDirectories);
        Assert.NotEmpty(cached);

        await sut.ResetRemoteCacheAsync();

        Assert.Empty(Directory.GetFiles(_scratch, "cached.sqlite", SearchOption.AllDirectories));
    }

    [PosixFact]
    public async Task TheGateIsReleased_EvenWhenACommandFails()
    {
        // A held semaphore would deadlock every later call — including the browser's.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"), cacheRoot: _scratch);

        await Assert.ThrowsAsync<DriveException>(() => sut.ExecuteAsync(["-c", "exit 3"]));

        var output = await sut.ExecuteAsync(["-c", "echo recovered"]);
        Assert.Contains("recovered", output);
    }

    [PosixFact]
    public async Task TheGateIsReleased_WhenACallIsCancelled()
    {

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"), cacheRoot: _scratch);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExecuteAsync(["-c", "echo hi"], cts.Token));

        var output = await sut.ExecuteAsync(["-c", "echo recovered"]);
        Assert.Contains("recovered", output);
    }
}
