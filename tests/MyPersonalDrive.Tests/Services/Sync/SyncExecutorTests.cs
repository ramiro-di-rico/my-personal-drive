using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncExecutorTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-executor-tests").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-executor-tests-{Guid.NewGuid():N}.db");
    private const string RemoteRoot = "/my-files/Docs";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_localRoot, recursive: true);
        File.Delete(_dbPath);
    }

    private static string FileEntry(string name, string content, string modifiedAt = "2026-01-01T00:00:00.000Z")
        => $$"""
            {
              "uid": "uid-{{name}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "{{modifiedAt}}",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{content.Length}},
                  "claimedModificationTime": "{{modifiedAt}}",
                  "claimedDigests": { "sha1": "hash-{{name}}" }
                }
              }
            }
            """;

    private static string FolderEntry(string name)
        => $$"""
            {
              "uid": "uid-{{name}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "folder", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;

    private async Task<SyncPair> CreatePairAsync(SyncStateStore store, SyncDirection direction = SyncDirection.RemoteToLocal, ConflictPolicy policy = ConflictPolicy.Ask)
        => await store.CreatePairAsync(RemoteRoot, _localRoot, direction, policy);

    /// <summary>
    /// Writes a local file with an mtime old enough that <see cref="LocalScanner"/> doesn't skip
    /// it as "possibly still being written" (its 2s settling guard).
    /// </summary>
    private string WriteSettledLocalFile(string relativePath, string content)
    {
        var absolutePath = Path.Combine(_localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
        File.SetLastWriteTimeUtc(absolutePath, DateTime.UtcNow.AddMinutes(-5));
        return absolutePath;
    }

    [Fact]
    public async Task RunAsync_DownloadsNewRemoteFile_AndSetsItsMtimeFromTheRemoteFingerprint()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToDownload);
        var downloadedPath = Path.Combine(_localRoot, "a.txt");
        Assert.True(File.Exists(downloadedPath));
        Assert.Equal("hello", await File.ReadAllTextAsync(downloadedPath));
        Assert.Equal(DateTime.Parse("2026-01-01T00:00:00.000Z").ToUniversalTime(), File.GetLastWriteTimeUtc(downloadedPath));
    }

    [Fact]
    public async Task RunAsync_CreatesLocalFoldersForNewRemoteFolders_Recursively()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FolderEntry("Photos")}]");
        executor.RespondForPath($"{RemoteRoot}/Photos", $"[{FileEntry("pic.jpg", "binary-ish")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "pic.jpg"), "binary-ish");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        Assert.True(Directory.Exists(Path.Combine(_localRoot, "Photos")));
        Assert.True(File.Exists(Path.Combine(_localRoot, "Photos", "pic.jpg")));
    }

    [Fact]
    public async Task RunAsync_RemovedRemotely_MovesLocalFileToTrash_NeverDeletesPermanently()
    {
        File.WriteAllText(Path.Combine(_localRoot, "gone.txt"), "still here locally");
        File.SetLastWriteTimeUtc(Path.Combine(_localRoot, "gone.txt"), DateTime.UtcNow.AddMinutes(-5));

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]"); // remote no longer has it

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        Assert.False(File.Exists(Path.Combine(_localRoot, "gone.txt")));
        var trashedFiles = Directory.GetFiles(_localRoot, "gone.txt", SearchOption.AllDirectories);
        var trashedFile = Assert.Single(trashedFiles);
        Assert.Contains(".mypersonaldrive-trash", trashedFile);
    }

    // ------------------------------------------------------------------ F2: TwoWay

    [Fact]
    public async Task RunAsync_TwoWay_NewLocalFile_IsUploadedIntoTheRightRemoteFolder()
    {
        WriteSettledLocalFile("notes.txt", "local only");

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");                                  // remote scan: empty
        executor.EnqueueOutput("");                                    // the upload
        executor.EnqueueOutput($"[{FileEntry("notes.txt", "local only")}]"); // baseline re-read

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToUpload);
        var upload = Assert.Single(executor.Calls, c => c.Arguments.Contains("upload"));
        Assert.Equal(["filesystem", "upload", "-c", "replace", Path.Combine(_localRoot, "notes.txt"), RemoteRoot], upload.Arguments);
    }

    [Fact]
    public async Task RunAsync_TwoWay_NewLocalFolder_IsCreatedRemotely()
    {
        Directory.CreateDirectory(Path.Combine(_localRoot, "Invoices"));

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");                          // remote scan: empty
        executor.EnqueueOutput("");                             // create-folder
        executor.EnqueueOutput($"[{FolderEntry("Invoices")}]"); // baseline re-read

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        var create = Assert.Single(executor.Calls, c => c.Arguments.Contains("create-folder"));
        Assert.Equal(["filesystem", "create-folder", RemoteRoot, "Invoices"], create.Arguments);
    }

    [Fact]
    public async Task RunAsync_TwoWay_RecordsABaseline_SoASecondRunWithNoChangesDoesNothing()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var first = await sut.RunAsync(pair);
        Assert.Equal(1, first.Stats.FilesToDownload);

        var baseline = await stateStore.GetBaselineAsync(pair.Id);
        var entry = Assert.Single(baseline.Values);
        Assert.Equal("a.txt", entry.RelativePath);
        Assert.NotNull(entry.RemoteAtSync);
        Assert.Equal("uid-a.txt", entry.RemoteAtSync!.NodeId);
        // The local side is re-read after the transfer, hash included (§5.4/§7).
        Assert.NotNull(entry.LocalAtSync);
        Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", entry.LocalAtSync!.ContentHash); // sha1("hello")

        // Nothing changed on either side since, so the plan must be empty — this is the whole
        // point of the baseline: without it, the second run would re-download or re-upload.
        var second = await sut.RunAsync(pair);
        Assert.Empty(second.Actions);
    }

    [Fact]
    public async Task RunAsync_TwoWay_LocalDeleteWithUntouchedRemote_TrashesRemote_NeverDeletesPermanently()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair); // establishes the baseline
        File.Delete(Path.Combine(_localRoot, "a.txt"));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.ToTrashRemote);
        var trash = Assert.Single(executor.Calls, c => c.Arguments.Contains("trash"));
        Assert.Equal(["filesystem", "trash", $"{RemoteRoot}/a.txt"], trash.Arguments);
        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("delete"));
    }

    [Fact]
    public async Task RunAsync_TwoWay_AfterTrashingRemote_LeavesNoBaselineRowBehind()
    {
        // Regression, found by the real-account integration run: TrashRemote falls through to the
        // shared baseline write, where both sides are now absent. Upserting that would leave a
        // row claiming the baseline knows a path that exists nowhere, costing the next run a
        // whole ClearBaseline item to undo — one wasted item and run per deletion.
        // Strictly ordered responses (one remote folder, so the call order is deterministic):
        // the remote scan must still see a.txt in run 2, and only the post-trash re-read sees it gone.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 1: scan
        executor.EnqueueOutput(args =>                               // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 2: scan — still there remotely
        executor.EnqueueOutput("");                                 // run 2: trash
        executor.EnqueueOutput("[]");                               // run 3: scan

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair); // baseline established
        File.Delete(Path.Combine(_localRoot, "a.txt"));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.ToTrashRemote);
        Assert.Empty(await stateStore.GetBaselineAsync(pair.Id));

        // ...and therefore a third run has nothing left to do at all.
        Assert.Empty((await sut.RunAsync(pair)).Actions);
    }

    [Fact]
    public async Task RunAsync_TwoWay_TrashRemote_ClearsTheBaseline_EvenIfTheRemoteStillListsTheTrashedFile()
    {
        // Reproduces the real flake (~1 real-account run in 3): Proton's listing is not
        // read-your-writes consistent after a `trash`, so a re-read issued right afterwards can
        // still report the node alive. The executor must not ask — a deletion's outcome is known
        // by construction — otherwise it records a baseline row claiming the remote copy survived.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 1: scan
        executor.EnqueueOutput(args =>                               // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 2: scan
        executor.EnqueueOutput("");                                 // run 2: trash succeeds
        // Any further listing in run 2 would be the stale re-read. Leaving the queue empty makes
        // the FakeCliExecutor throw if the executor asks — the assertion is "it must not ask".
        executor.EnqueueOutput("[]");                               // run 3: scan, now consistent

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);
        File.Delete(Path.Combine(_localRoot, "a.txt"));
        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.ToTrashRemote);
        Assert.Empty(await stateStore.GetBaselineAsync(pair.Id));
        Assert.Equal(SyncPairStatus.Ok, (await stateStore.GetPairAsync(pair.Id))!.LastStatus);

        // No listing was issued between the trash and the end of the run.
        var callsAfterTrash = executor.Calls.SkipWhile(c => !c.Arguments.Contains("trash")).Skip(1);
        Assert.DoesNotContain(callsAfterTrash, c => c.Arguments.Contains("list"));
    }

    [Fact]
    public async Task RunAsync_TwoWay_BothSidesDeleted_ClearsTheBaselineRow()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);
        Assert.NotEmpty(await stateStore.GetBaselineAsync(pair.Id));

        // Both sides gone: no transfer, no delete — just forget the row (SyncOperation.ClearBaseline).
        File.Delete(Path.Combine(_localRoot, "a.txt"));
        executor.RespondForPath(RemoteRoot, "[]");

        await sut.RunAsync(pair);

        Assert.Empty(await stateStore.GetBaselineAsync(pair.Id));
        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("trash"));
    }

    [Fact]
    public async Task RunAsync_TwoWay_FolderAlreadyOnBothSides_IsRecordedAsAFolderInTheBaseline()
    {
        // A folder present on both sides with no baseline yields UpdateBaselineOnly — an operation
        // that implies nothing about whether the node is a file, so IsFolder has to come from the
        // scans. Recording it as a file would store an empty fingerprint for a folder that exists.
        Directory.CreateDirectory(Path.Combine(_localRoot, "Shared"));

        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FolderEntry("Shared")}]");
        executor.RespondForPath($"{RemoteRoot}/Shared", "[]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        var entry = Assert.Single(await stateStore.GetBaselineAsync(pair.Id)).Value;
        Assert.Equal("Shared", entry.RelativePath);
        Assert.True(entry.IsFolder);
        Assert.NotNull(entry.LocalAtSync);  // the folder was found on disk, not looked up as a file
        Assert.NotNull(entry.RemoteAtSync);
    }

    // ------------------------------------------------------------------ F2: conflicts (§5.6)

    [Fact]
    public async Task RunAsync_KeepBothConflict_RenamesTheLocalCopyAside_DownloadsRemote_AndUploadsTheRenamedCopy()
    {
        // Both sides have 'a.txt' with different content and no baseline: §5.2's first row.
        WriteSettledLocalFile("a.txt", "mine");

        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "theirs")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "theirs");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var plan = await sut.RunAsync(pair);

        Assert.Single(plan.Conflicts);

        // The remote version now holds the original name...
        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));

        // ...and the local one survives under a timestamped name, uploaded rather than discarded.
        var conflictCopy = Assert.Single(Directory.GetFiles(_localRoot, "a (local conflict*"));
        Assert.Equal("mine", await File.ReadAllTextAsync(conflictCopy));
        Assert.Contains(executor.Calls, c => c.Arguments.Contains("upload") && c.Arguments.Any(a => a.Contains("local conflict")));
    }

    [Fact]
    public async Task RunAsync_AskPolicy_ParksConflictsAsDurableRows_WithoutTouchingEitherSide()
    {
        WriteSettledLocalFile("a.txt", "mine");

        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "theirs")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay, ConflictPolicy.Ask);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var plan = await sut.RunAsync(pair);

        Assert.Single(plan.Conflicts);
        Assert.Empty(plan.Actions); // Ask resolves nothing automatically

        var parked = Assert.Single(await stateStore.GetConflictActionsAsync(pair.Id));
        Assert.Equal("a.txt", parked.RelativePath);
        Assert.Equal(SyncQueueState.Conflict, parked.State);

        // Neither side was touched, and the pair reports it needs the user.
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));
        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("download") || c.Arguments.Contains("upload"));
        var updated = await stateStore.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.PartialFailure, updated!.LastStatus);
        Assert.Contains("conflict", updated.LastError!);
    }

    // ------------------------------------------------------------------ F2: retries (§7)

    [Fact]
    public async Task RunAsync_TransientFailure_SchedulesARetry_InsteadOfFailingTheRowPermanently()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(_ => throw new CliException("download", 1, "", "connection reset", "connection reset", CliErrorKind.Network));

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service), clock);

        await sut.RunAsync(pair);

        // Still Pending (so a later run picks it up), but held back until the backoff elapses.
        Assert.Empty(await stateStore.GetPendingActionsAsync(pair.Id, clock.GetUtcNow()));
        clock.Advance(SyncRetryPolicy.Backoff[0]);
        var retryable = Assert.Single(await stateStore.GetPendingActionsAsync(pair.Id, clock.GetUtcNow()));
        Assert.Equal(1, retryable.AttemptCount);
    }

    [Fact]
    public async Task RunAsync_AuthFailure_FailsTheRowPermanentlyAndAbortsTheRest()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}, {FileEntry("b.txt", "world")}]");
        executor.EnqueueOutput(_ => throw new CliException("download", 1, "", "You need to login first", "You need to login first", CliErrorKind.NotAuthenticated));

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service), clock);

        await sut.RunAsync(pair);

        // b.txt was never attempted — it would have failed identically, at ~3.5s per CLI process.
        Assert.Equal(1, executor.Calls.Count(c => c.Arguments.Contains("download")));

        // a.txt got no retry (a retry can't fix an expired session), so the only row still
        // pending is the untouched b.txt — which is exactly right: once the user signs in again,
        // the next run picks it up with no attempts wasted.
        var stillPending = await stateStore.GetPendingActionsAsync(pair.Id, clock.GetUtcNow().AddDays(1));
        Assert.Equal("b.txt", Assert.Single(stillPending).RelativePath);
        var updated = await stateStore.GetPairAsync(pair.Id);
        Assert.Contains("stopped early", updated!.LastError!);
    }

    [Fact]
    public async Task PreviewAsync_NeverCallsDownloadOrWritesLocalFiles()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        var plan = await sut.PreviewAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToDownload);
        Assert.False(File.Exists(Path.Combine(_localRoot, "a.txt")));
        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("download"));
    }

    [Fact]
    public async Task RunAsync_Success_UpdatesPairStatusToOk()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        var updated = await stateStore.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.Ok, updated!.LastStatus);
        Assert.NotNull(updated.LastSyncAt);
    }

    [Fact]
    public async Task RunAsync_ActionFailure_UpdatesPairStatusToPartialFailure_ButStillCompletesOtherActions()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}, {FileEntry("b.txt", "world")}]");
        executor.EnqueueOutput(_ => throw new CliException("download", 1, "", "disk full", "disk full")); // a.txt fails
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "b.txt"), "world");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await sut.RunAsync(pair);

        var updated = await stateStore.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.PartialFailure, updated!.LastStatus);
        Assert.False(File.Exists(Path.Combine(_localRoot, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_localRoot, "b.txt"))); // the other action still ran
    }
}
