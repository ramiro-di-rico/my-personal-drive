using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

public class SyncPairPauseTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-pause").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-pause-{Guid.NewGuid():N}.db");
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

    private async Task<(SyncPanelViewModel Panel, SyncPairViewModel Row, FakeCliExecutor Cli, SyncStateStore Store)> BuildAsync()
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath(RemoteRoot, "[]");

        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(cli);
        var executor = new SyncExecutor(service, store, new LocalScanner(), new RemoteScanner(service));
        await store.CreatePairAsync(RemoteRoot, _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        await panel.InitializeAsync();
        return (panel, Assert.Single(panel.Pairs), cli, store);
    }

    [Fact]
    public async Task ANewPair_StartsUnpaused()
    {
        var (_, row, _, _) = await BuildAsync();

        Assert.False(row.IsPaused);
        Assert.Equal("⏸️", row.PauseGlyph);
    }

    [Fact]
    public async Task Pausing_PersistsAndFlipsTheControl()
    {
        var (_, row, _, store) = await BuildAsync();

        await row.TogglePauseCommand.ExecuteAsync();

        Assert.True(row.IsPaused);
        Assert.Equal("▶️", row.PauseGlyph);
        Assert.Contains("Resume", row.PauseTooltip);
        Assert.True(Assert.Single(await store.GetPairsAsync()).IsPaused);
    }

    [Fact]
    public async Task Resuming_PutsItBack()
    {
        var (_, row, _, store) = await BuildAsync();
        await row.TogglePauseCommand.ExecuteAsync();

        await row.TogglePauseCommand.ExecuteAsync();

        Assert.False(row.IsPaused);
        Assert.False(Assert.Single(await store.GetPairsAsync()).IsPaused);
    }

    [Fact]
    public async Task ThePauseSurvivesReopeningThePanel()
    {
        var (panel, row, _, _) = await BuildAsync();
        await row.TogglePauseCommand.ExecuteAsync();

        await panel.InitializeAsync();

        Assert.True(Assert.Single(panel.Pairs).IsPaused);
    }

    [Fact]
    public async Task APausedPair_SaysSoInsteadOfClaimingToBeUpToDate()
    {
        // "Up to date" on a frozen pair becomes a lie the moment anything changes, so the pause has
        // to lead — it's what decides whether the rest of the status is still being kept true.
        var (_, row, _, _) = await BuildAsync();
        await row.SyncNowCommand.ExecuteAsync();
        Assert.StartsWith("Up to date", row.StatusText);

        await row.TogglePauseCommand.ExecuteAsync();

        Assert.StartsWith("Paused —", row.StatusText);
        Assert.Contains("Up to date", row.StatusText); // the underlying state is still reported
    }

    [Fact]
    public async Task APausedPair_CanStillBeSyncedByHand()
    {
        // Pause means "stop doing this on your own", not "refuse my explicit instructions".
        var (_, row, cli, _) = await BuildAsync();
        await row.TogglePauseCommand.ExecuteAsync();

        await row.SyncNowCommand.ExecuteAsync();

        Assert.Contains(cli.Calls, c => c.Arguments.Contains("list"));
        Assert.StartsWith("Paused —", row.StatusText); // and it stays paused afterwards
    }
}
