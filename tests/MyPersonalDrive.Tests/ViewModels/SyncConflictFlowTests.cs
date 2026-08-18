using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The conflicts flow as the panel drives it, with the dialog replaced by a function returning
/// decisions — the same seam the window fills in. Covers what the view-model owns: the badge counts,
/// that dismissing the dialog changes nothing, and that one file failing doesn't abandon the rest.
/// </summary>
public class SyncConflictFlowTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-conflict-flow").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-conflict-flow-{Guid.NewGuid():N}.db");
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

    private static string FileEntry(string name, string content)
        => $$"""
            {
              "uid": "uid-{{name}}", "name": { "ok": true, "value": "{{name}}" },
              "type": "file", "modificationTime": "2026-01-01T00:00:00.000Z",
              "activeRevision": { "ok": true, "value": {
                "claimedSize": {{content.Length}},
                "claimedModificationTime": "2026-01-01T00:00:00.000Z",
                "claimedDigests": { "sha1": "hash-{{name}}" } } }
            }
            """;

    private void WriteSettled(string name, string content)
    {
        var path = Path.Combine(_localRoot, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
    }

    private async Task<(SyncPanelViewModel Panel, SyncPairViewModel Row, FakeCliExecutor Cli, SyncStateStore Store)> BuildWithTwoConflictsAsync()
    {
        WriteSettled("a.txt", "my a");
        WriteSettled("b.txt", "my b");

        var cli = new FakeCliExecutor();
        cli.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "their a")}, {FileEntry("b.txt", "their b")}]");

        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(cli);
        var executor = new SyncExecutor(service, store, new LocalScanner(), new RemoteScanner(service));
        await store.CreatePairAsync(RemoteRoot, _localRoot, SyncDirection.TwoWay, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        await panel.InitializeAsync();
        var row = Assert.Single(panel.Pairs);

        await row.SyncNowCommand.ExecuteAsync();
        return (panel, row, cli, store);
    }

    [Fact]
    public async Task AfterASyncFindsConflicts_TheRowAdvertisesThem()
    {
        var (_, row, _, _) = await BuildWithTwoConflictsAsync();

        Assert.True(row.HasConflicts);
        Assert.Equal(2, row.ConflictCount);
        Assert.Equal("⚠ 2 conflicts", row.ConflictText);
        Assert.True(row.ResolveConflictsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheCountSurvivesReopeningThePanel()
    {
        var (panel, _, _, _) = await BuildWithTwoConflictsAsync();

        await panel.InitializeAsync(); // as if the window were closed and opened again

        Assert.Equal(2, Assert.Single(panel.Pairs).ConflictCount);
    }

    [Fact]
    public async Task DismissingTheDialog_ResolvesNothing()
    {
        var (_, row, cli, store) = await BuildWithTwoConflictsAsync();
        row.RequestConflictResolutionsAsync = _ =>
            Task.FromResult<IReadOnlyDictionary<long, ConflictResolution>>(new Dictionary<long, ConflictResolution>());

        await row.ResolveConflictsCommand.ExecuteAsync();

        Assert.Equal(2, (await store.GetConflictActionsAsync(1)).Count);
        Assert.Equal("my a", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("upload"));
    }

    [Fact]
    public async Task DecidingOnOneFileOnly_LeavesTheOtherParked()
    {
        var (_, row, _, store) = await BuildWithTwoConflictsAsync();
        var conflicts = await store.GetConflictActionsAsync(1);
        var chosen = conflicts.Single(c => c.RelativePath == "a.txt");

        row.RequestConflictResolutionsAsync = _ =>
            Task.FromResult<IReadOnlyDictionary<long, ConflictResolution>>(
                new Dictionary<long, ConflictResolution> { [chosen.Id] = ConflictResolution.KeepLocal });

        await row.ResolveConflictsCommand.ExecuteAsync();

        var remaining = Assert.Single(await store.GetConflictActionsAsync(1));
        Assert.Equal("b.txt", remaining.RelativePath);
        Assert.Equal(1, row.ConflictCount);
    }

    [Fact]
    public async Task OneFileFailing_DoesNotAbandonTheOthers()
    {
        var (_, row, cli, store) = await BuildWithTwoConflictsAsync();
        var conflicts = await store.GetConflictActionsAsync(1);

        // 'a.txt' resolves via download; make that download fail, and leave 'b.txt' to upload fine.
        cli.EnqueueOutput(_ => throw new CliException("download", 1, "", "net down", "net down", CliErrorKind.Network));

        var errors = new List<string>();
        row.OnError = errors.Add;
        row.RequestConflictResolutionsAsync = _ =>
            Task.FromResult<IReadOnlyDictionary<long, ConflictResolution>>(
                new Dictionary<long, ConflictResolution>
                {
                    [conflicts.Single(c => c.RelativePath == "a.txt").Id] = ConflictResolution.KeepRemote,
                    [conflicts.Single(c => c.RelativePath == "b.txt").Id] = ConflictResolution.KeepLocal,
                });

        await row.ResolveConflictsCommand.ExecuteAsync();

        Assert.Contains(errors, e => e.Contains("a.txt"));
        var stillParked = Assert.Single(await store.GetConflictActionsAsync(1));
        Assert.Equal("a.txt", stillParked.RelativePath);       // the failure stays for another try
        Assert.Contains(cli.Calls, c => c.Arguments.Contains("upload")); // b.txt still went through
    }

    [Fact]
    public async Task RetryFailed_IsOnlyOfferedWhenSomethingActuallyFailed()
    {
        var (_, row, _, store) = await BuildWithTwoConflictsAsync();
        Assert.False(row.HasFailures);
        Assert.False(row.RetryFailedCommand.CanExecute(null));

        await store.EnqueueActionsAsync(1, [new SyncAction(SyncOperation.DownloadFile, "dead.txt", null, 1, 1000)], DateTimeOffset.UtcNow);
        var queued = Assert.Single(await store.GetPendingActionsAsync(1));
        await store.MarkFailedAsync(queued.Id, "gave up", nextAttemptAt: null);
        await row.RefreshOutstandingAsync();

        Assert.True(row.HasFailures);
        await row.RetryFailedCommand.ExecuteAsync();

        Assert.False(row.HasFailures);
        Assert.Single(await store.GetPendingActionsAsync(1));
    }

    [Fact]
    public async Task PartialFailure_WithTransientBackoff_AdvertisesFailures_AndRecoversViaRetry()
    {
        var (_, row, _, store) = await BuildWithTwoConflictsAsync();
        Assert.False(row.HasFailures);

        await store.EnqueueActionsAsync(1, [new SyncAction(SyncOperation.DownloadFile, "retryable.txt", null, 1, 1000)], DateTimeOffset.UtcNow);
        var queued = Assert.Single(await store.GetPendingActionsAsync(1));
        await store.MarkFailedAsync(queued.Id, "connection timeout", nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(5));
        await store.UpdatePairStatusAsync(1, DateTimeOffset.UtcNow, SyncPairStatus.PartialFailure, "1 action(s) failed");
        var pair = await store.GetPairAsync(1);
        await row.RefreshOutstandingAsync();

        // Should advertise failure even when nextAttemptAt is in the future
        Assert.True(row.HasFailures);
        Assert.True(row.RetryFailedCommand.CanExecute(null));

        await row.RetryFailedCommand.ExecuteAsync();

        Assert.False(row.HasFailures);
        var revived = Assert.Single(await store.GetPendingActionsAsync(1));
        Assert.Null(revived.LastError);
        Assert.Equal(0, revived.AttemptCount);
    }

    [Fact]
    public async Task PausedPair_WithPartialFailure_CanRecover()
    {
        var (_, row, _, store) = await BuildWithTwoConflictsAsync();
        await row.TogglePauseCommand.ExecuteAsync();
        Assert.True(row.IsPaused);

        await store.UpdatePairStatusAsync(1, DateTimeOffset.UtcNow, SyncPairStatus.PartialFailure, "1 action(s) failed");
        await store.EnqueueActionsAsync(1, [new SyncAction(SyncOperation.DownloadFile, "doc.txt", null, 1, 1000)], DateTimeOffset.UtcNow);
        var queued = Assert.Single(await store.GetPendingActionsAsync(1));
        await store.MarkFailedAsync(queued.Id, "busy", nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(2));
        await row.RefreshOutstandingAsync();

        Assert.True(row.HasFailures);
        Assert.Contains("Partial failure", row.StatusText);
        Assert.StartsWith("Paused —", row.StatusText);

        await row.RetryFailedCommand.ExecuteAsync();

        Assert.False(row.HasFailures);
        Assert.StartsWith("Paused —", row.StatusText);
        var pending = Assert.Single(await store.GetPendingActionsAsync(1));
        Assert.Equal(0, pending.AttemptCount);
    }

    [Fact]
    public async Task PairInErrorStatus_AdvertisesFailures_AndCanRecover()
    {
        var (_, row, _, store) = await BuildWithTwoConflictsAsync();

        await store.UpdatePairStatusAsync(1, DateTimeOffset.UtcNow, SyncPairStatus.Error, "Proton Drive CLI connection lost");
        await row.RefreshOutstandingAsync();

        Assert.True(row.HasFailures);
        Assert.True(row.RetryFailedCommand.CanExecute(null));

        await row.RetryFailedCommand.ExecuteAsync();

        Assert.False(row.HasFailures);
    }
}
