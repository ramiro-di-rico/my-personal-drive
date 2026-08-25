using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using Xunit;
using Xunit.Abstractions;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// Exercises F2's TwoWay sync against the real CLI and a real account. The unit tests pin the
/// *command strings* we send; this pins that the CLI actually accepts them and does what we
/// assumed — in particular `upload -c replace` overwriting a remote file in place, and
/// `create-folder`/`trash` behaving as the executor expects.
///
/// Everything happens inside one throwaway remote folder, and <see cref="Dispose"/> trashes it
/// (never `delete` — docs/PLAN-LOCAL-SYNC.md §11's safety rule applies to our own test debris too).
/// </summary>
/// <remarks>
/// Both real-CLI test classes share one xUnit collection so they never run concurrently. xUnit
/// parallelizes across classes by default, and concurrent `proton-drive` processes intermittently
/// crash on the CLI's own SQLite cache (docs/PLAN-LOCAL-SYNC.md Appendix A #11) — which made these
/// tests fail differently on every run until they were serialized.
/// </remarks>
[Collection("RealCli")]
public sealed class RealCliTwoWaySyncTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _remoteRoot = $"/my-files/f2-integration-{Guid.NewGuid():N}"[..40];
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-f2-integration").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-f2-integration-{Guid.NewGuid():N}.db");
    private readonly ProtonDriveService _service;
    private readonly ProtonDriveProvider _provider;
    private readonly bool _enabled = Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnvironmentVariable) == "1";

    public RealCliTwoWaySyncTests(ITestOutputHelper output)
    {
        _output = output;
        var cliPath = Environment.GetEnvironmentVariable("MYPERSONALDRIVE_CLI")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "proton-drive");
        _service = new ProtonDriveService(new ProtonDriveCliExecutor(new FixedPathLocator(cliPath)));
        _provider = new ProtonDriveProvider(_service);
        _service.CommandStarted += (_, e) =>
        {
            _output.WriteLine($"$ {e.CommandText}");
            if (e.CommandText.Contains(" download ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _downloadCount);
            }

            if (e.CommandText.Contains(" upload ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _uploadCount);
            }
        };
    }

    private int _downloadCount;
    private int _uploadCount;

    /// <summary>
    /// How many `filesystem download` invocations have been issued. The real executor doesn't record
    /// calls the way the fake does, so this counts them off the command stream — which is what makes
    /// "the rename transferred nothing" an assertion rather than an assumption.
    /// </summary>
    private int CountDownloads() => Volatile.Read(ref _downloadCount);

    private int CountUploads() => Volatile.Read(ref _uploadCount);

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
            // Test debris in the temp folder isn't worth failing the run over.
        }
    }

    [IntegrationFact]
    public async Task TwoWay_FullLifecycle_AgainstTheRealAccount()
    {
        var stateStore = new SyncStateStore(_dbPath);
        var sut = new SyncExecutor(_provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(_provider));

        // ---------- arrange: a remote-only file, a local-only file, and a local-only folder
        var rootName = _remoteRoot[("/my-files/".Length)..];
        await _service.CreateFolderAsync("/my-files", rootName);

        var seedPath = Path.Combine(_localRoot, "remote-only.txt");
        await File.WriteAllTextAsync(seedPath, "came from the cloud");
        await _service.UploadFilesAsync([seedPath], _remoteRoot);
        File.Delete(seedPath); // now it exists only remotely

        WriteSettled("local-only.txt", "came from this machine");
        Directory.CreateDirectory(Path.Combine(_localRoot, "local-folder"));

        var pair = await stateStore.CreatePairAsync(_remoteRoot, _localRoot, SyncDirection.TwoWay, ConflictPolicy.KeepBoth);

        // ---------- run 1: one file each way, plus a folder created remotely
        var first = await sut.RunAsync(pair);
        _output.WriteLine($"run 1: {string.Join(", ", first.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");

        Assert.Equal(1, first.Stats.FilesToDownload);
        Assert.Equal(1, first.Stats.FilesToUpload);
        Assert.Equal(1, first.Stats.FoldersToCreateRemotely);

        // The download landed, with the remote's claimed mtime restored (Appendix A #6).
        var downloaded = Path.Combine(_localRoot, "remote-only.txt");
        Assert.Equal("came from the cloud", await File.ReadAllTextAsync(downloaded));

        var remoteAfterFirst = await ListRemoteAsync();
        Assert.Contains("local-only.txt", remoteAfterFirst.Keys);
        Assert.Contains("local-folder", remoteAfterFirst.Keys);
        Assert.True(remoteAfterFirst["local-folder"].IsFolder);

        // The remote's claimed mtime for the uploaded file must match what we set locally — this
        // is what makes the baseline comparison stable instead of drifting every run.
        var localMtime = File.GetLastWriteTimeUtc(Path.Combine(_localRoot, "local-only.txt"));
        Assert.Equal(localMtime, remoteAfterFirst["local-only.txt"].ModifiedAt!.Value.UtcDateTime, TimeSpan.FromSeconds(2));

        // ---------- run 2: the baseline should make this a no-op
        var second = await sut.RunAsync(pair);
        _output.WriteLine($"run 2: {second.Actions.Count} action(s)");
        Assert.Empty(second.Actions);
        Assert.Empty(second.Conflicts);

        // ---------- run 3: a local edit is uploaded over the remote copy (`upload -c replace`)
        WriteSettled("local-only.txt", "edited on this machine, definitely longer than before");
        var third = await sut.RunAsync(pair);
        _output.WriteLine($"run 3: {string.Join(", ", third.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");

        Assert.Equal(1, third.Stats.FilesToUpload);
        var remoteAfterThird = await ListRemoteAsync();

        // Exactly one copy — `replace` must not have left a "keep both" sibling behind.
        Assert.Single(remoteAfterThird.Keys.Where(k => k.StartsWith("local-only", StringComparison.Ordinal)));
        Assert.Equal("edited on this machine, definitely longer than before".Length, remoteAfterThird["local-only.txt"].Size);

        // ---------- a real remote rename must move the local file, not re-download it (§11)
        // This is the claim worth checking against the live CLI rather than a mock: that the `uid`
        // really does survive `filesystem rename`, which is what the whole optimization rests on.
        await _service.RenameItemAsync($"{_remoteRoot}/local-only.txt", "renamed-remotely.txt");
        var downloadsBeforeRename = CountDownloads();

        var renameRun = await sut.RunAsync(pair);
        _output.WriteLine($"rename run: {string.Join(", ", renameRun.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");

        Assert.Equal(1, renameRun.Stats.FilesToMoveLocally);
        Assert.Equal(0, renameRun.Stats.FilesToDownload);
        Assert.Equal(downloadsBeforeRename, CountDownloads()); // not a single byte re-transferred
        Assert.True(File.Exists(Path.Combine(_localRoot, "renamed-remotely.txt")));
        Assert.False(File.Exists(Path.Combine(_localRoot, "local-only.txt")));
        Assert.Equal("edited on this machine, definitely longer than before",
            await File.ReadAllTextAsync(Path.Combine(_localRoot, "renamed-remotely.txt")));

        // And it converges: the run after the move has nothing left to do.
        Assert.Empty((await sut.RunAsync(pair)).Actions);

        // ---------- the mirror image: a *local* rename must move it on Proton Drive (backlog B4)
        // This is the half with no stable local id, so it rests on §11.3's content match. Worth doing
        // against the live CLI because it is also the only exercise of `filesystem rename` driven by
        // the engine rather than by the test.
        File.Move(Path.Combine(_localRoot, "renamed-remotely.txt"), Path.Combine(_localRoot, "renamed-locally.txt"));
        File.SetLastWriteTimeUtc(Path.Combine(_localRoot, "renamed-locally.txt"), DateTime.UtcNow.AddMinutes(-5));
        var uploadsBeforeLocalRename = CountUploads();

        var localRenameRun = await sut.RunAsync(pair);
        _output.WriteLine($"local rename run: {string.Join(", ", localRenameRun.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");

        Assert.Equal(1, localRenameRun.Stats.FilesToMoveRemotely);
        Assert.Equal(0, localRenameRun.Stats.FilesToUpload);
        Assert.Equal(uploadsBeforeLocalRename, CountUploads()); // nothing re-uploaded
        var afterLocalRename = await ListRemoteAsync();
        Assert.Contains("renamed-locally.txt", afterLocalRename.Keys);
        Assert.DoesNotContain("renamed-remotely.txt", afterLocalRename.Keys);

        Assert.Empty((await sut.RunAsync(pair)).Actions);

        // ---------- run 4: a local delete trashes the remote copy, never permanently deletes it
        File.Delete(downloaded);
        var fourth = await sut.RunAsync(pair);
        _output.WriteLine($"run 4: {string.Join(", ", fourth.Actions.Select(a => $"{a.Operation} {a.RelativePath}"))}");

        Assert.Equal(1, fourth.Stats.ToTrashRemote);

        // ---------- run 5, immediately: the resurrection check (Appendix A #15)
        // Deliberately before the convergence poll below, so the staleness window is still open —
        // this is the moment a stale listing would make §5.2 read the trashed file as "new
        // remotely" and download it back. (If the listing happens to have converged already the
        // assertion passes trivially; it can't fail spuriously.)
        var fifth = await sut.RunAsync(pair);
        _output.WriteLine($"run 5 (immediately after the trash): {fifth.Actions.Count} action(s)");
        Assert.Empty(fifth.Actions);
        Assert.False(File.Exists(downloaded), "the deleted file was resurrected by a stale listing");

        Assert.True(await EventuallyGoneFromRemoteAsync("remote-only.txt"),
            "the trashed file was still listed after waiting for the listing to converge");

        // ---------- and the pair ends healthy, with a baseline covering what survived
        var finalPair = await stateStore.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.Ok, finalPair!.LastStatus);
        Assert.Null(finalPair.LastError);

        var baseline = await stateStore.GetBaselineAsync(pair.Id);
        // 'local-only.txt' now lives under the name the remote rename gave it — and the baseline
        // followed the file rather than stranding a row at the old path.
        Assert.Equal(["local-folder", "renamed-locally.txt"], baseline.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.NotNull(baseline["renamed-locally.txt"].RemoteAtSync!.NodeId);   // the CLI's stable uid
        Assert.NotNull(baseline["renamed-locally.txt"].LocalAtSync!.ContentHash); // our own SHA-1
    }

    private void WriteSettled(string relativePath, string content)
    {
        var absolutePath = Path.Combine(_localRoot, relativePath);
        File.WriteAllText(absolutePath, content);
        // Backdated so LocalScanner's "still being written" guard doesn't skip it this cycle.
        File.SetLastWriteTimeUtc(absolutePath, DateTime.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// A listing issued right after a `trash` still returns the trashed node a good fraction of
    /// the time — measured convergence was ~7s, which is on the same order as the ~3.5s a single
    /// CLI process takes to start, so asserting it immediately was a coin flip (it failed ~2 runs
    /// in 3). Polls instead of assuming. See docs/PLAN-LOCAL-SYNC.md Appendix A #15.
    /// </summary>
    private async Task<bool> EventuallyGoneFromRemoteAsync(string name, int attempts = 4)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (!(await ListRemoteAsync()).ContainsKey(name))
            {
                _output.WriteLine($"'{name}' gone from the listing on attempt {attempt}");
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
