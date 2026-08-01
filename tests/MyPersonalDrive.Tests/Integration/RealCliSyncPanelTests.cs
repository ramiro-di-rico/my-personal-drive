using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using Xunit.Abstractions;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// Drives the Sync panel's view-models exactly as the window does — same services, same real CLI,
/// same `Request*Async` callbacks — and walks the whole Add pair → Preview → Run now flow.
///
/// This is the automatable part of F1/F2's outstanding UI click-through. What it does *not* cover
/// is the Avalonia layer itself: the XAML bindings, the dialogs' own controls, and the
/// `StorageProvider` folder picker. Synthetic input cannot reach the app in this machine's Wayland
/// session (neither pointer nor keyboard — see the Status section in docs/PLAN-LOCAL-SYNC.md), so
/// that last mile still needs either a human or a headless X server.
/// </summary>
/// <remarks>
/// Both real-CLI test classes share one xUnit collection so they never run concurrently. xUnit
/// parallelizes across classes by default, and concurrent `proton-drive` processes intermittently
/// crash on the CLI's own SQLite cache (docs/PLAN-LOCAL-SYNC.md Appendix A #11) — which made these
/// tests fail differently on every run until they were serialized.
/// </remarks>
[Collection("RealCli")]
public sealed class RealCliSyncPanelTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _remoteRoot = $"/my-files/ui-panel-{Guid.NewGuid():N}"[..32];
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-ui-panel").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-ui-panel-{Guid.NewGuid():N}.db");
    private readonly ProtonDriveService _service;
    private readonly bool _enabled = Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnvironmentVariable) == "1";

    public RealCliSyncPanelTests(ITestOutputHelper output)
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
    public async Task AddPair_Preview_RunNow_TheFlowTheSyncWindowDrives()
    {
        // ---------- arrange: a real remote folder with a file and a subfolder
        var rootName = _remoteRoot[("/my-files/".Length)..];
        await _service.CreateFolderAsync("/my-files", rootName);
        var seedPath = Path.Combine(_localRoot, "seed.txt");
        await File.WriteAllTextAsync(seedPath, "hello from the panel test");
        await _service.UploadFilesAsync([seedPath], _remoteRoot);
        File.Delete(seedPath);
        await _service.CreateFolderAsync(_remoteRoot, "nested");

        var stateStore = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(_service, stateStore, new LocalScanner(), new RemoteScanner(_service));
        var panel = new SyncPanelViewModel(stateStore, executor, new SyncCrashRecovery(stateStore));

        // ---------- the empty state the window opens into
        await panel.RecoverFromPreviousRunAsync();
        await panel.InitializeAsync();
        Assert.Empty(panel.Pairs);

        // ---------- a bad remote path is rejected without creating anything (§12 validation)
        panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("my-files/no-leading-slash", _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask));
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Empty(panel.Pairs);
        Assert.Contains("absolute path", panel.StatusMessage);

        // ---------- refusing to sync the home directory
        panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest(_remoteRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SyncDirection.RemoteToLocal, ConflictPolicy.Ask));
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Empty(panel.Pairs);
        Assert.Contains("home directory", panel.StatusMessage);

        // ---------- cancelling the dialog is a no-op
        panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(null);
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Empty(panel.Pairs);

        // ---------- the real thing
        panel.RequestNewPairAsync = () => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest(_remoteRoot, _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask));
        await panel.AddPairCommand.ExecuteAsync();

        var row = Assert.Single(panel.Pairs);
        Assert.Equal(_remoteRoot, row.RemotePath);
        Assert.Equal("Remote → Local", row.DirectionText);
        Assert.Equal("Never synced", row.StatusText);
        _output.WriteLine($"pair row: {row.RemotePath} | {row.DirectionText} | {row.StatusText}");

        // ---------- adding the same pair again is refused, naming what it clashes with. The §12
        // overlap check now catches this before the UNIQUE constraint does, so the message names the
        // paths rather than talking about "that combination" (the SqliteException handler is still
        // there as the backstop for a race between two windows).
        await panel.AddPairCommand.ExecuteAsync();
        Assert.Single(panel.Pairs);
        Assert.Contains("already synced", panel.StatusMessage);

        // ---------- Preview, declining to run: the plan is real, and nothing is touched
        SyncPlan? previewed = null;
        row.RequestPreviewConfirmationAsync = (plan, warnings) =>
        {
            previewed = plan;
            Assert.Empty(warnings); // a few bytes into a temp folder: no space warning expected
            return Task.FromResult(false);
        };
        await row.PreviewCommand.ExecuteAsync();

        Assert.NotNull(previewed);
        _output.WriteLine($"preview: {string.Join(", ", previewed!.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");
        Assert.Equal(1, previewed.Stats.FilesToDownload);
        Assert.Equal(1, previewed.Stats.FoldersToCreateLocally);
        Assert.Empty(Directory.GetFileSystemEntries(_localRoot)); // dry run really is dry
        Assert.Equal("Never synced", row.StatusText);

        // ---------- Preview again, this time accepting: "Run now" performs the sync
        row.RequestPreviewConfirmationAsync = (_, _) => Task.FromResult(true);
        await row.PreviewCommand.ExecuteAsync();

        Assert.Equal("hello from the panel test", await File.ReadAllTextAsync(Path.Combine(_localRoot, "seed.txt")));
        Assert.True(Directory.Exists(Path.Combine(_localRoot, "nested")));
        Assert.StartsWith("Up to date", row.StatusText);
        _output.WriteLine($"after run: {row.StatusText}");

        // ---------- and the row survives a panel reload, as it would on reopening the window
        await panel.InitializeAsync();
        var reloaded = Assert.Single(panel.Pairs);
        Assert.StartsWith("Up to date", reloaded.StatusText);

        // ---------- Remove takes it out of both the list and the database
        await reloaded.RemoveCommand.ExecuteAsync();
        Assert.Empty(panel.Pairs);
        Assert.Empty(await stateStore.GetPairsAsync());
    }

    private sealed class FixedPathLocator(string path) : IProtonDriveCliLocator
    {
        public string Locate() => path;
    }
}
