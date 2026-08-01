using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// Drives the scheduler through <see cref="SyncScheduler.PumpOnceAsync"/> rather than its timer
/// loop, so every assertion is deterministic — the loop is only that method plus a delay. The whole
/// stack underneath is real (executor, scanners, store) over a fake CLI.
/// </summary>
public class SyncSchedulerTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-scheduler-tests").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-scheduler-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00Z");
    private const string RemoteRoot = "/my-files/Docs";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_localRoot, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Harness(
        SyncScheduler Scheduler, FakeCliExecutor Cli, SyncStateStore Store, FakeTimeProvider Clock);

    private async Task<Harness> BuildAsync(bool authenticated = true, bool paused = false)
    {
        var clock = new FakeTimeProvider(T0);
        var cli = new FakeCliExecutor();
        var service = new ProtonDriveService(cli);
        var store = new SyncStateStore(_dbPath);
        var suppressor = new SyncEchoSuppressor(clock);
        var executor = new SyncExecutor(service, store, new LocalScanner(), new RemoteScanner(service), clock, suppressor);

        var pair = await store.CreatePairAsync(RemoteRoot, _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        if (paused)
        {
            await store.SetPairPausedAsync(pair.Id, true);
        }

        var scheduler = new SyncScheduler(store, executor, suppressor, () => authenticated, clock, TimeSpan.FromSeconds(1));
        return new Harness(scheduler, cli, store, clock);
    }

    private static int ListCalls(FakeCliExecutor cli) => cli.Calls.Count(c => c.Arguments.Contains("list"));

    [Fact]
    public async Task ANeverRunPair_IsSyncedOnTheFirstTick()
    {
        var h = await BuildAsync();
        h.Cli.RespondForPath(RemoteRoot, "[]");

        Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, ListCalls(h.Cli));

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task WithoutASession_NothingRunsAndNoProcessIsSpawned()
    {
        // §6.4's global pause. Every attempt would cost a ~3.5s process just to fail.
        var h = await BuildAsync(authenticated: false);
        h.Cli.RespondForPath(RemoteRoot, "[]");

        Assert.False(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Empty(h.Cli.Calls);

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task APausedPair_IsNeverSynced()
    {
        var h = await BuildAsync(paused: true);
        h.Cli.RespondForPath(RemoteRoot, "[]");

        Assert.False(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Empty(h.Cli.Calls);

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task AfterACycle_ThePairWaitsOutItsInterval_ThenRunsAgain()
    {
        var h = await BuildAsync();
        h.Cli.RespondForPath(RemoteRoot, "[]");

        await h.Scheduler.PumpOnceAsync(CancellationToken.None);
        Assert.Equal(1, ListCalls(h.Cli));

        // Immediately after, and well before the 5-minute floor: nothing.
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, ListCalls(h.Cli));

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(2, ListCalls(h.Cli));

        await h.Scheduler.DisposeAsync();
    }

    /// <summary>
    /// Fails <paramref name="times"/> consecutive cycles, waiting out each one's backoff. Note the
    /// early backoff steps (1, 2, 4 min) are subsumed by the 5-minute poll floor — per §6.4 the
    /// backoff *extends* the interval rather than replacing it, so it only starts to bite from the
    /// fourth consecutive failure on.
    /// </summary>
    private static async Task FailCyclesAsync(Harness h, int times)
    {
        for (var i = 1; i <= times; i++)
        {
            h.Cli.EnqueueOutput(_ => throw new CliException("list", 1, "", "boom", "boom", CliErrorKind.Network));
            var due = SyncSchedulePolicy.ErrorBackoff(i - 1);
            h.Clock.Advance(due > SyncSchedulePolicy.MinInterval ? due : SyncSchedulePolicy.MinInterval);
            Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None), $"failure {i} did not run");
        }
    }

    [Fact]
    public async Task ARepeatedlyFailingPair_BacksOffInsteadOfRetryingEveryTick()
    {
        var h = await BuildAsync();
        await FailCyclesAsync(h, 6); // 6 consecutive errors → the 30-minute ceiling
        var callsSoFar = ListCalls(h.Cli);

        // 29 minutes later it is still not due: the backoff now exceeds the 5-minute floor.
        h.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.False(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(callsSoFar, ListCalls(h.Cli));

        h.Cli.EnqueueOutput("[]");
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None));

        // And every failure was recorded for the user, with the wait spelled out.
        var logs = await h.Store.GetRecentLogsAsync(null, 20);
        Assert.Contains(logs, l => l.Level == SyncLogLevel.Error && l.Message.Contains("Automatic sync failed"));
        Assert.Contains(logs, l => l.Message.Contains("next try in 30 min"));

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task ASuccessfulCycle_ClearsTheErrorBackoff()
    {
        var h = await BuildAsync();
        await FailCyclesAsync(h, 6); // now waiting 30 minutes between attempts

        h.Cli.EnqueueOutput("[]");
        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None));

        // Recovered: back to the ordinary 5-minute floor rather than another 30-minute wait.
        h.Cli.EnqueueOutput("[]");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await h.Scheduler.PumpOnceAsync(CancellationToken.None));

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task ARemovedPair_StopsBeingSynced()
    {
        var h = await BuildAsync();
        h.Cli.RespondForPath(RemoteRoot, "[]");
        await h.Scheduler.PumpOnceAsync(CancellationToken.None);

        var pair = Assert.Single(await h.Store.GetPairsAsync());
        await h.Store.DeletePairAsync(pair.Id);

        // Past the pair-refresh interval so the scheduler re-reads, and past the poll interval so
        // it would otherwise be due.
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await h.Scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, ListCalls(h.Cli));

        await h.Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task StartThenStop_LeavesNoLoopRunning()
    {
        var h = await BuildAsync();
        h.Cli.RespondForPath(RemoteRoot, "[]");

        h.Scheduler.Start();
        Assert.True(h.Scheduler.IsRunning);

        await h.Scheduler.StopAsync();
        Assert.False(h.Scheduler.IsRunning);
    }
}
