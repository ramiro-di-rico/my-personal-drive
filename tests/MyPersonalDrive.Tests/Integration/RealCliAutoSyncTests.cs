using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// F3's actual claim, against the real CLI and account: <b>nobody asks for a sync and one happens
/// anyway</b>. The test writes a local file and then only ticks the scheduler — it never calls
/// <c>SyncExecutor.RunAsync</c> — so a passing run means the whole chain worked: real
/// `FileSystemWatcher` event → <see cref="ChangeDebouncer"/> → pair marked dirty →
/// <see cref="SyncSchedulePolicy"/> deeming it due → executor → upload.
///
/// The clock is fake so the 2s debounce and the 5-minute floor don't cost real time, but the
/// filesystem events and the CLI are entirely real.
/// </summary>
/// <remarks>
/// Shares the "RealCli" collection: concurrent `proton-drive` processes crash each other on the
/// CLI's own SQLite cache (docs/PLAN-LOCAL-SYNC.md Appendix A #11).
/// </remarks>
[Collection("RealCli")]
public sealed class RealCliAutoSyncTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _remoteRoot = $"/my-files/f3-auto-{Guid.NewGuid():N}"[..28];
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-f3-auto").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-f3-auto-{Guid.NewGuid():N}.db");
    private readonly ProtonDriveService _service;
    private readonly bool _enabled = Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnvironmentVariable) == "1";

    public RealCliAutoSyncTests(ITestOutputHelper output)
    {
        _output = output;
        var cliPath = Environment.GetEnvironmentVariable("MYPERSONALDRIVE_CLI")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "proton-drive");
        _service = new ProtonDriveService(new ProtonDriveCliExecutor(new FixedPathLocator(cliPath)));
        _service.CommandStarted += (_, e) => _output.WriteLine($"$ {e.CommandText}");
    }

    public void Dispose()
    {
        if (_enabled)
        {
            try
            {
                _service.TrashItemAsync(_remoteRoot).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Could not trash '{_remoteRoot}': {ex.Message} — trash it manually.");
            }
        }

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

    [IntegrationFact]
    public async Task ALocalEdit_IsSyncedWithoutAnyoneAskingForIt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var rootName = _remoteRoot[("/my-files/".Length)..];
        await _service.CreateFolderAsync("/my-files", rootName);

        var store = new SyncStateStore(_dbPath);
        var suppressor = new SyncEchoSuppressor(clock);
        var executor = new SyncExecutor(_service, store, new LocalScanner(), new RemoteScanner(_service), clock, suppressor);
        await store.CreatePairAsync(_remoteRoot, _localRoot, SyncDirection.TwoWay, ConflictPolicy.KeepBoth);

        await using var scheduler = new SyncScheduler(store, executor, suppressor, () => true, clock, TimeSpan.FromMilliseconds(50));

        // First tick: the pair has never run, so it syncs (nothing to move) and the watcher starts.
        Assert.True(await scheduler.PumpOnceAsync(CancellationToken.None));
        Assert.Empty((await ListRemoteAsync()).Keys);

        // Now the user creates a file. Nothing below calls RunAsync.
        var localFile = Path.Combine(_localRoot, "typed-by-the-user.txt");
        await File.WriteAllTextAsync(localFile, "the scheduler should notice this on its own");
        File.SetLastWriteTimeUtc(localFile, DateTime.UtcNow.AddMinutes(-5)); // past LocalScanner's settling guard

        var uploaded = await PumpUntilAsync(scheduler, clock,
            async () => (await ListRemoteAsync()).ContainsKey("typed-by-the-user.txt"));

        Assert.True(uploaded, "the local file was never picked up by the scheduler");
        _output.WriteLine("the file reached Proton Drive with no explicit sync request");

        // And the engine's own work didn't feed itself: once quiet, further ticks find nothing to do.
        clock.Advance(SyncEchoSuppressor.DefaultWindow + TimeSpan.FromMinutes(10));
        await scheduler.PumpOnceAsync(CancellationToken.None);
        var settled = await store.GetPairsAsync();
        Assert.Equal(SyncPairStatus.Ok, settled[0].LastStatus);
    }

    /// <summary>
    /// Real watcher events arrive on real time, so their arrival has to be waited for; the debounce
    /// and the poll interval are on the fake clock, so they're advanced rather than slept through.
    /// </summary>
    private async Task<bool> PumpUntilAsync(SyncScheduler scheduler, FakeTimeProvider clock, Func<Task<bool>> condition, int attempts = 8)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await Task.Delay(400); // let inotify deliver
            clock.Advance(ChangeDebouncer.DefaultQuietPeriod + SyncSchedulePolicy.MinInterval);
            await scheduler.PumpOnceAsync(CancellationToken.None);

            if (await condition())
            {
                _output.WriteLine($"condition met on attempt {attempt}");
                return true;
            }
        }

        return false;
    }

    private async Task<Dictionary<string, DriveItem>> ListRemoteAsync()
        => (await _service.LoadFolderAsync(_remoteRoot)).ToDictionary(item => item.Name, item => item);

    private sealed class FixedPathLocator(string path) : IProtonDriveCliLocator
    {
        public string Locate() => path;
    }
}
