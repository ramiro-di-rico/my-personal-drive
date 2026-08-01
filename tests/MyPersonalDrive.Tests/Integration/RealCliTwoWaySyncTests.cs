using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
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
    private readonly bool _enabled = Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnvironmentVariable) == "1";

    public RealCliTwoWaySyncTests(ITestOutputHelper output)
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
            // Test debris in the temp folder isn't worth failing the run over.
        }
    }

    [IntegrationFact]
    public async Task TwoWay_FullLifecycle_AgainstTheRealAccount()
    {
        var stateStore = new SyncStateStore(_dbPath);
        var sut = new SyncExecutor(_service, stateStore, new LocalScanner(), new RemoteScanner(_service));

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
        Assert.Equal(["local-folder", "local-only.txt"], baseline.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.NotNull(baseline["local-only.txt"].RemoteAtSync!.NodeId);   // the CLI's stable uid
        Assert.NotNull(baseline["local-only.txt"].LocalAtSync!.ContentHash); // our own SHA-1
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
