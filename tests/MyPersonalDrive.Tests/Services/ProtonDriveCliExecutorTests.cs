using MyPersonalDrive.Services;
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

    [PosixFact]
    public async Task ConcurrentCalls_AreSerialized_SoTwoCliProcessesNeverOverlap()
    {
        // Why this matters (docs/PLAN-LOCAL-SYNC.md §9 / Appendix A #11): concurrent `proton-drive`
        // processes crash each other on the CLI's own SQLite cache about one time in three. Three
        // unrelated parts of the app spawn CLI processes — the scheduler, the sync panel, and the
        // file browser — so the guarantee has to live in the executor they all share.
        //
        // The probe is a `mkdir` lock: mkdir on an existing directory fails, so any overlap makes a
        // command exit non-zero, which the executor surfaces as a CliException.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"));
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
    public async Task TheGateIsReleased_EvenWhenACommandFails()
    {
        // A held semaphore would deadlock every later call — including the browser's.

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"));

        await Assert.ThrowsAsync<CliException>(() => sut.ExecuteAsync(["-c", "exit 3"]));

        var output = await sut.ExecuteAsync(["-c", "echo recovered"]);
        Assert.Contains("recovered", output);
    }

    [PosixFact]
    public async Task TheGateIsReleased_WhenACallIsCancelled()
    {

        var sut = new ProtonDriveCliExecutor(new FixedPathLocator("/bin/sh"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExecuteAsync(["-c", "echo hi"], cts.Token));

        var output = await sut.ExecuteAsync(["-c", "echo recovered"]);
        Assert.Contains("recovered", output);
    }
}
