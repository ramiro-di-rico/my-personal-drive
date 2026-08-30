using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
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

    /// <summary>
    /// <paramref name="uid"/> and <paramref name="hash"/> default to being derived from the name,
    /// which is convenient until a test needs a node to keep its identity across a rename — that's
    /// precisely what move detection correlates on, so those tests pass them explicitly.
    /// </summary>
    private static string FileEntry(string name, string content, string modifiedAt = "2026-01-01T00:00:00.000Z", string? uid = null, string? hash = null)
        => $$"""
            {
              "uid": "{{uid ?? $"uid-{name}"}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "{{modifiedAt}}",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{content.Length}},
                  "claimedModificationTime": "{{modifiedAt}}",
                  "claimedDigests": { "sha1": "{{hash ?? $"hash-{name}"}}" }
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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToUpload);
        var upload = Assert.Single(executor.Calls, c => c.Arguments.Contains("upload"));
        Assert.Equal(["filesystem", "upload", "-f", "replace", "-d", "replace", Path.Combine(_localRoot, "notes.txt"), RemoteRoot], upload.Arguments);
    }

    /// <summary>
    /// Regression, reported live: two providers both switched to LocalToRemote for a folder whose
    /// files already existed independently on every side re-uploaded everything on every single
    /// cycle instead of just the one file that actually needed it. Root cause: LocalScanner never
    /// computes a content hash, so a file whose local/remote mtimes disagree (unrelated timestamps,
    /// since the two copies were never actually related by an upload this app did) looked "changed"
    /// forever by SyncReconciler's own size+mtime fallback — HashAmbiguousUploadCandidatesAsync now
    /// fills in the local hash for exactly that ambiguous case, so a real content match is
    /// recognized and the file is correctly left alone.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_LocalToRemote_APreExistingFileWithMismatchedMtimeButMatchingContent_IsNotReUploaded()
    {
        WriteSettledLocalFile("a.txt", "hello");
        var realSha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("hello")));

        var executor = new FakeCliExecutor();
        // The remote copy has the exact same content (so the same real sha1) but a completely
        // unrelated modification time — exactly what "already existed independently on both
        // sides" looks like, since no upload by this app ever related the two timestamps.
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello", modifiedAt: "2020-01-01T00:00:00.000Z", hash: realSha1)}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.LocalToRemote);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var plan = await sut.PreviewAsync(pair);

        Assert.Equal(0, plan.Stats.FilesToUpload);
        Assert.Empty(plan.Actions);
    }

    /// <summary>A genuinely different file must still upload — the hash check isn't a blanket "never re-upload".</summary>
    [Fact]
    public async Task PreviewAsync_LocalToRemote_AFileThatActuallyChanged_StillUploads()
    {
        WriteSettledLocalFile("a.txt", "hello world!"); // same length (12) as "hello-remote", different content
        var unrelatedSha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("hello-remote")));

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello-remote", modifiedAt: "2020-01-01T00:00:00.000Z", hash: unrelatedSha1)}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.LocalToRemote);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var plan = await sut.PreviewAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToUpload);
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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
    public async Task RunAsync_TwoWay_AStaleListingAfterTrash_DoesNotResurrectTheDeletedFile()
    {
        // The resurrection path from Appendix A #15: run 2 trashes the remote copy, then run 3's
        // scan is still stale and reports it alive. With the baseline row already cleared, §5.2
        // would read L=absent/R=present/B=absent as "new remotely" and re-download the file the
        // user just deleted. SyncEchoSuppressor is what stops that.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 1: scan
        executor.EnqueueOutput(args =>                               // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 2: scan
        executor.EnqueueOutput("");                                 // run 2: trash
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 3: scan — STALE

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        File.Delete(Path.Combine(_localRoot, "a.txt"));
        await sut.RunAsync(pair);

        var third = await sut.RunAsync(pair);

        Assert.Empty(third.Actions);
        Assert.False(File.Exists(Path.Combine(_localRoot, "a.txt")), "the deleted file came back");
        Assert.Equal(1, executor.Calls.Count(c => c.Arguments.Contains("download"))); // only run 1's
    }

    [Fact]
    public async Task PreviewAsync_AfterATrash_DoesNotOfferToDownloadTheDeletedFileBack()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 1: scan
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "hello");
            return "";
        });
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // run 2: scan
        executor.EnqueueOutput("");                                 // run 2: trash
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]"); // preview: scan — STALE

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        File.Delete(Path.Combine(_localRoot, "a.txt"));
        await sut.RunAsync(pair);

        var plan = await sut.PreviewAsync(pair);

        Assert.Empty(plan.Actions);
        Assert.Equal(0, plan.Stats.FilesToDownload);
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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);

        var entry = Assert.Single(await stateStore.GetBaselineAsync(pair.Id)).Value;
        Assert.Equal("Shared", entry.RelativePath);
        Assert.True(entry.IsFolder);
        Assert.NotNull(entry.LocalAtSync);  // the folder was found on disk, not looked up as a file
        Assert.NotNull(entry.RemoteAtSync);
    }

    [Fact]
    public async Task RunAsync_APersistentlyFailingFile_CostsAConstantAmountOfWorkPerRun()
    {
        // Regression for the quadratic-work bug. Every run re-proposes the failed download, and
        // blind enqueueing left one extra row per run, each with a fresh retry budget: measured CLI
        // attempts went 1, 3, 6, 10, 15. Automatic sync runs ~288 times a day, so this had to
        // become flat before F4 added more surface on top of it.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "hello")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider), clock);

        var attemptsPerRun = new List<int>();
        var attemptsBefore = 0;
        for (var run = 0; run < 5; run++)
        {
            // Plenty of queued failures, so every row in the run fails for the same transient
            // reason rather than running out of canned responses.
            for (var i = 0; i < 10; i++)
            {
                executor.EnqueueOutput(_ => throw new DriveException("download", 1, "", "net down", "net down", DriveErrorKind.Network));
            }

            await sut.RunAsync(pair);
            var attemptsNow = executor.Calls.Count(c => c.Arguments.Contains("download"));
            attemptsPerRun.Add(attemptsNow - attemptsBefore);
            attemptsBefore = attemptsNow;
            clock.Advance(TimeSpan.FromMinutes(10)); // past the retry backoff
        }

        // Flat, not triangular: one attempt per run, from the single surviving row.
        Assert.Equal([1, 1, 1, 1, 1], attemptsPerRun);

        // And exactly one row exists for that action, however many runs went by.
        var pendingOrFailed = await stateStore.GetPendingActionsAsync(pair.Id);
        Assert.True(pendingOrFailed.Count <= 1, $"expected at most one live row, found {pendingOrFailed.Count}");
    }

    [Fact]
    public async Task RunAsync_ANodeWhoseNameCannotExistLocally_IsSkippedAndExplained()
    {
        // Backlog B1. Before this, such a node produced a relative path indistinguishable from a
        // nested one, so the download was aimed at a path the CLI couldn't resolve and failed every
        // run forever with "Node not found" — a permanently broken action and an inscrutable error.
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("ok.txt", "fine")}, {FileEntry("in/voice.pdf", "cannot")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "ok.txt"), "fine");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var plan = await sut.RunAsync(pair);

        // Only the usable file is planned, and the run is clean rather than partially failed.
        Assert.Equal(1, plan.Stats.FilesToDownload);
        Assert.Equal("ok.txt", Assert.Single(plan.Actions).RelativePath);
        Assert.Equal(SyncPairStatus.Ok, (await stateStore.GetPairAsync(pair.Id))!.LastStatus);

        // And the user is told why the other one never appears, with what to do about it.
        var logs = await stateStore.GetRecentLogsAsync(pair.Id, 50);
        var warning = Assert.Single(logs, l => l.Level == SyncLogLevel.Warning);
        Assert.Contains("in/voice.pdf", warning.Message);
        Assert.Contains("rename it there", warning.Message);
    }

    /// <summary>
    /// Regression test for the P1-P5 adversarial review: once P3 taught <c>RemoteScanner</c> to
    /// also skip case-colliding siblings on a case-insensitive provider, the log message here
    /// still hardcoded the unmappable-name explanation ("its name contains '/'") for every skip —
    /// factually wrong for a name with no slash at all. Uses
    /// <see cref="CaseInsensitivePathsDecorator"/> since Proton itself never collides.
    /// </summary>
    [Fact]
    public async Task RunAsync_ACaseCollidingRemoteName_IsSkippedWithAnAccurateExplanation_NotTheSlashOne()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FolderEntry("Report")}, {FolderEntry("report")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var provider = new CaseInsensitivePathsDecorator(new ProtonDriveProvider(new ProtonDriveService(executor)));
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);

        var logs = await stateStore.GetRecentLogsAsync(pair.Id, 50);
        var warnings = logs.Where(l => l.Level == SyncLogLevel.Warning).ToList();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.DoesNotContain("contains '/'", w.Message));
        Assert.Contains(warnings, w => w.Message.Contains("collides", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ F4: progress (§12)

    [Fact]
    public async Task RunAsync_ReportsProgressPerAction_BeforeEachOneStarts()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "aaa")}, {FileEntry("b.txt", "bbb")}]");
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            var captured = name;
            executor.EnqueueOutput(args =>
            {
                File.WriteAllText(Path.Combine(args[3], captured), captured);
                return "";
            });
        }

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var reports = new List<SyncExecutor.SyncProgress>();
        sut.Progress += (_, p) => reports.Add(p);

        await sut.RunAsync(pair);

        // Two actions, so two "starting" reports plus a final one, and every report knows the total.
        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Equal(2, r.Total));

        // Reported before the work, so the first report is at 0 — a counter that only moved on
        // completion would leave the slowest item invisible for exactly as long as it mattered.
        Assert.Equal(0, reports[0].Completed);
        Assert.Equal(SyncOperation.DownloadFile, reports[0].Operation);
        Assert.Equal("a.txt", reports[0].RelativePath);

        Assert.Equal(1, reports[1].Completed);
        Assert.Equal("b.txt", reports[1].RelativePath);

        // The last one has no action attached: it says the queue is drained.
        Assert.Equal(2, reports[^1].Completed);
        Assert.Null(reports[^1].Operation);
    }

    [Fact]
    public async Task RunAsync_WithNothingToDo_ReportsNoProgressAtAll()
    {
        // An idle cycle must not make the row flicker through a progress message.
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, "[]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var reports = new List<SyncExecutor.SyncProgress>();
        sut.Progress += (_, p) => reports.Add(p);

        await sut.RunAsync(pair);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task RunAsync_ProgressCountsFailuresToo_SoItNeverStalls()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "aaa")}, {FileEntry("b.txt", "bbb")}]");
        executor.EnqueueOutput(_ => throw new DriveException("download", 1, "", "boom", "boom", DriveErrorKind.Network));
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "b.txt"), "bbb");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var reports = new List<SyncExecutor.SyncProgress>();
        sut.Progress += (_, p) => reports.Add(p);

        await sut.RunAsync(pair);

        // The counter measures progress through the queue, not successes — an action that failed is
        // just as done being attempted, and stalling the count would misreport a run that is moving.
        Assert.Equal(2, reports[^1].Completed);
    }

    // ------------------------------------------------------------------ F5: remote moves (§11)

    [Fact]
    public async Task RunAsync_TwoWay_ARemoteRename_MovesTheLocalFileWithoutDownloadingItAgain()
    {
        const string uid = "uid-stable";
        const string hash = "hash-stable";

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("x.pdf", "content", uid: uid, hash: hash)}]"); // run 1: scan
        executor.EnqueueOutput(args =>                                                       // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "x.pdf"), "content");
            return "";
        });
        // Run 2: the same node — same uid, same hash — now reported under a different name.
        executor.EnqueueOutput($"[{FileEntry("y.pdf", "content", uid: uid, hash: hash)}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        Assert.True(File.Exists(Path.Combine(_localRoot, "x.pdf")));
        var downloadsAfterFirstRun = executor.Calls.Count(c => c.Arguments.Contains("download"));

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToMoveLocally);
        Assert.False(File.Exists(Path.Combine(_localRoot, "x.pdf")));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_localRoot, "y.pdf")));

        // The point of the whole feature: no bytes moved over the network.
        Assert.Equal(downloadsAfterFirstRun, executor.Calls.Count(c => c.Arguments.Contains("download")));

        // Nor was it treated as a deletion — the file must not be sitting in the local trash.
        Assert.Empty(Directory.GetFiles(_localRoot, "x.pdf", SearchOption.AllDirectories));

        // The baseline follows the file: one row, at the new path.
        var baseline = await stateStore.GetBaselineAsync(pair.Id);
        Assert.Equal(["y.pdf"], baseline.Keys);
        Assert.Equal(uid, baseline["y.pdf"].RemoteAtSync!.NodeId);
    }

    [Fact]
    public async Task RunAsync_TwoWay_AfterAMove_TheNextRunHasNothingLeftToDo()
    {
        const string uid = "uid-stable";
        const string hash = "hash-stable";

        // Strictly ordered responses: RespondForPath would answer run 1's scan too, which is not
        // what this scenario is about.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("x.pdf", "content", uid: uid, hash: hash)}]"); // run 1: scan
        executor.EnqueueOutput(args =>                                                       // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "x.pdf"), "content");
            return "";
        });
        executor.EnqueueOutput($"[{FileEntry("y.pdf", "content", uid: uid, hash: hash)}]"); // run 2: scan, renamed
        executor.EnqueueOutput($"[{FileEntry("y.pdf", "content", uid: uid, hash: hash)}]"); // run 3: scan, settled

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        await sut.RunAsync(pair); // performs the move

        // Converged: if the baseline hadn't been rewritten correctly this would re-download or
        // re-delete forever.
        Assert.Empty((await sut.RunAsync(pair)).Actions);
        Assert.Equal(SyncPairStatus.Ok, (await stateStore.GetPairAsync(pair.Id))!.LastStatus);
    }

    [Fact]
    public async Task RunAsync_TwoWay_AMoveIntoANewFolder_CreatesTheFolderFirst()
    {
        const string uid = "uid-stable";
        const string hash = "hash-stable";

        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("x.pdf", "content", uid: uid, hash: hash)}]"); // run 1: scan root
        executor.EnqueueOutput(args =>                                                       // run 1: download
        {
            File.WriteAllText(Path.Combine(args[3], "x.pdf"), "content");
            return "";
        });
        // Run 2: the node now lives inside a folder that doesn't exist locally yet. The scanner
        // walks one depth level per wave with concurrency 1, so the order is root then archive.
        executor.EnqueueOutput($"[{FolderEntry("archive")}]");
        executor.EnqueueOutput($"[{FileEntry("x.pdf", "content", uid: uid, hash: hash)}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        var plan = await sut.RunAsync(pair);

        // The folder creation is planned ahead of the move, or the move would have nowhere to land.
        var operations = plan.Actions.Select(a => a.Operation).ToList();
        Assert.Contains(SyncOperation.CreateLocalFolder, operations);
        Assert.True(operations.IndexOf(SyncOperation.CreateLocalFolder) < operations.IndexOf(SyncOperation.RenameLocal));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_localRoot, "archive", "x.pdf")));
    }

    // ------------------------------------------------------------------ B4: local moves (§11.3)

    /// <summary>
    /// Gets a pair to a state where 'x.pdf' is in sync at the root, then returns everything needed to
    /// move it locally and run again.
    /// </summary>
    private async Task<(SyncPair Pair, SyncExecutor Sut, FakeCliExecutor Cli, SyncStateStore Store)> SyncedFileAsync(string content = "the content")
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("x.pdf", content, uid: "uid-x", hash: "hash-x")}]");
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "x.pdf"), content);
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        return (pair, sut, executor, stateStore);
    }

    private void MoveLocally(string from, string to)
    {
        var source = Path.Combine(_localRoot, from);
        var destination = Path.Combine(_localRoot, to);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(-5)); // past the settling guard
    }

    [Fact]
    public async Task RunAsync_TwoWay_ALocalRename_RenamesRemotelyWithoutReuploading()
    {
        var (pair, sut, cli, store) = await SyncedFileAsync();
        var uploadsBefore = cli.Calls.Count(c => c.Arguments.Contains("upload"));

        MoveLocally("x.pdf", "y.pdf");
        cli.EnqueueOutput($"[{FileEntry("x.pdf", "the content", uid: "uid-x", hash: "hash-x")}]"); // scan: still at the old name
        cli.EnqueueOutput("");                                                                     // the rename
        cli.EnqueueOutput($"[{FileEntry("y.pdf", "the content", uid: "uid-x", hash: "hash-x")}]"); // baseline re-read

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToMoveRemotely);
        var rename = Assert.Single(cli.Calls, c => c.Arguments.Contains("rename"));
        Assert.Equal(["filesystem", "rename", $"{RemoteRoot}/x.pdf", "y.pdf"], rename.Arguments);

        // Nothing re-uploaded, and no move call: same parent, so a rename alone suffices.
        Assert.Equal(uploadsBefore, cli.Calls.Count(c => c.Arguments.Contains("upload")));
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("move"));

        var baseline = await store.GetBaselineAsync(pair.Id);
        Assert.Equal(["y.pdf"], baseline.Keys);
    }

    [Fact]
    public async Task RunAsync_TwoWay_ALocalMoveIntoAFolder_MovesRemotelyKeepingTheName()
    {
        var (pair, sut, cli, store) = await SyncedFileAsync();

        Directory.CreateDirectory(Path.Combine(_localRoot, "archive"));
        MoveLocally("x.pdf", Path.Combine("archive", "x.pdf"));

        cli.EnqueueOutput($"[{FileEntry("x.pdf", "the content", uid: "uid-x", hash: "hash-x")}]"); // scan root
        cli.EnqueueOutput("");                                                                     // create-folder archive
        cli.EnqueueOutput("");                                                                     // the move
        cli.EnqueueOutput($"[{FolderEntry("archive")}]");                                           // baseline re-reads
        cli.EnqueueOutput($"[{FileEntry("x.pdf", "the content", uid: "uid-x", hash: "hash-x")}]");

        var plan = await sut.RunAsync(pair);

        Assert.Equal(1, plan.Stats.FilesToMoveRemotely);
        var move = Assert.Single(cli.Calls, c => c.Arguments.Contains("move"));
        Assert.Equal(["filesystem", "move", $"{RemoteRoot}/x.pdf", $"{RemoteRoot}/archive"], move.Arguments);

        // Same name, so no rename was needed.
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("rename"));
        Assert.Equal(["archive", "archive/x.pdf"], (await store.GetBaselineAsync(pair.Id)).Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunAsync_TwoWay_ALocalMoveThatAlsoRenames_IssuesMoveThenRename()
    {
        // The case that needs both commands: `move` keeps the name, `rename` keeps the parent. Move
        // first, so the node reaches its destination folder before taking its final name.
        var (pair, sut, cli, _) = await SyncedFileAsync();

        Directory.CreateDirectory(Path.Combine(_localRoot, "archive"));
        MoveLocally("x.pdf", Path.Combine("archive", "renamed.pdf"));

        cli.EnqueueOutput($"[{FileEntry("x.pdf", "the content", uid: "uid-x", hash: "hash-x")}]"); // scan root
        cli.EnqueueOutput("");                                                                     // create-folder
        cli.EnqueueOutput("");                                                                     // move
        cli.EnqueueOutput("");                                                                     // rename
        cli.EnqueueOutput($"[{FolderEntry("archive")}]");
        cli.EnqueueOutput($"[{FileEntry("renamed.pdf", "the content", uid: "uid-x", hash: "hash-x")}]");

        await sut.RunAsync(pair);

        var move = Assert.Single(cli.Calls, c => c.Arguments.Contains("move"));
        var rename = Assert.Single(cli.Calls, c => c.Arguments.Contains("rename"));
        Assert.Equal(["filesystem", "move", $"{RemoteRoot}/x.pdf", $"{RemoteRoot}/archive"], move.Arguments);

        // The rename targets the node at its *new* parent, under the name the move left it with.
        Assert.Equal(["filesystem", "rename", $"{RemoteRoot}/archive/x.pdf", "renamed.pdf"], rename.Arguments);
        Assert.True(cli.Calls.IndexOf(move) < cli.Calls.IndexOf(rename));
    }

    [Fact]
    public async Task RunAsync_TwoWay_NothingVanishedLocally_HashesNothing()
    {
        // Hashing is the one expensive step, so it must not happen just because files are new. With an
        // empty baseline nothing has disappeared, so there is no move to look for.
        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, "[]");
        WriteSettledLocalFile("a.txt", "aaa");
        WriteSettledLocalFile("b.txt", "bbb");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        var plan = await sut.PreviewAsync(pair);

        Assert.Equal(2, plan.Stats.FilesToUpload);
        Assert.Equal(0, plan.Stats.FilesToMoveRemotely);
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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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

    [Fact]
    public async Task RunAsync_PrunesTheLog_EvenWhenTheScanItselfFails()
    {
        // Housekeeping runs before anything that can throw, on purpose: a pair whose scan fails
        // every cycle is exactly the one generating the most log noise, and if pruning sat after the
        // scan it would be the one pair that never got tidied.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var executor = new FakeCliExecutor();
        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider), clock);

        await stateStore.LogAsync(pair.Id, SyncLogLevel.Info, null, "ancient history", clock.GetUtcNow());
        clock.Advance(TimeSpan.FromDays(40));
        executor.EnqueueOutput(_ => throw new DriveException("list", 1, "", "net down", "net down", DriveErrorKind.Network));

        await Assert.ThrowsAsync<DriveException>(() => sut.RunAsync(pair));

        Assert.Empty(await stateStore.GetRecentLogsAsync(pair.Id, 100));
    }

    // ------------------------------------------------------------------ F4: manual resolution (§5.6)

    /// <summary>Parks one Ask conflict on 'a.txt' and returns its queue row, ready to resolve.</summary>
    private async Task<(SyncPair Pair, QueuedSyncAction Conflict, SyncExecutor Sut, FakeCliExecutor Cli, SyncStateStore Store)> ParkedConflictAsync()
    {
        WriteSettledLocalFile("a.txt", "my local version");

        var executor = new FakeCliExecutor();
        executor.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "their remote version")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay, ConflictPolicy.Ask);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        var conflict = Assert.Single(await stateStore.GetConflictActionsAsync(pair.Id));
        return (pair, conflict, sut, executor, stateStore);
    }

    [Fact]
    public async Task ResolveConflict_KeepLocal_UploadsTheLocalVersionAndClosesTheRow()
    {
        var (pair, conflict, sut, cli, store) = await ParkedConflictAsync();

        await sut.ResolveConflictAsync(pair, conflict, ConflictResolution.KeepLocal);

        var upload = Assert.Single(cli.Calls, c => c.Arguments.Contains("upload"));
        Assert.Contains("-f", upload.Arguments);
        Assert.Contains("replace", upload.Arguments);
        Assert.Equal("my local version", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));
        Assert.Empty(await store.GetConflictActionsAsync(pair.Id));
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("download"));
    }

    [Fact]
    public async Task ResolveConflict_KeepRemote_DownloadsOverTheLocalVersion()
    {
        var (pair, conflict, sut, cli, store) = await ParkedConflictAsync();
        cli.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "their remote version");
            return "";
        });

        await sut.ResolveConflictAsync(pair, conflict, ConflictResolution.KeepRemote);

        Assert.Equal("their remote version", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));
        Assert.Empty(await store.GetConflictActionsAsync(pair.Id));
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("upload"));
    }

    [Fact]
    public async Task ResolveConflict_KeepBoth_PreservesBothVersions()
    {
        var (pair, conflict, sut, cli, store) = await ParkedConflictAsync();
        cli.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "a.txt"), "their remote version");
            return "";
        });

        await sut.ResolveConflictAsync(pair, conflict, ConflictResolution.KeepBoth);

        // The remote version takes the original name...
        Assert.Equal("their remote version", await File.ReadAllTextAsync(Path.Combine(_localRoot, "a.txt")));
        // ...and the local one survives under a stamped name, and is uploaded rather than stranded.
        var copy = Assert.Single(Directory.GetFiles(_localRoot, "a (local conflict*"));
        Assert.Equal("my local version", await File.ReadAllTextAsync(copy));
        Assert.Contains(cli.Calls, c => c.Arguments.Contains("upload") && c.Arguments.Any(a => a.Contains("local conflict")));
        Assert.Empty(await store.GetConflictActionsAsync(pair.Id));
    }

    [Fact]
    public async Task ResolveConflict_DoesNotWalkTheRemoteTree()
    {
        // Resolving one file must not cost a full remote walk — that's ~3.5s per folder (Appendix A
        // #11a) for a decision the user has already made. Only the conflicting file's own parent is
        // touched: once before, and once after an upload because that mints a new revision whose
        // fingerprint §7 requires re-reading.
        WriteSettledLocalFile("a.txt", "my local version");

        var cli = new FakeCliExecutor();
        cli.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "their remote version")}, {FolderEntry("sub")}]");
        cli.RespondForPath($"{RemoteRoot}/sub", $"[{FileEntry("deep.txt", "irrelevant")}]");

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay, ConflictPolicy.Ask);
        var service = new ProtonDriveService(cli);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);
        var conflict = Assert.Single(await stateStore.GetConflictActionsAsync(pair.Id));
        var callsBefore = cli.Calls.Count;

        await sut.ResolveConflictAsync(pair, conflict, ConflictResolution.KeepLocal);

        var duringResolve = cli.Calls.Skip(callsBefore).ToList();
        Assert.DoesNotContain(duringResolve, c => c.Arguments.Contains($"{RemoteRoot}/sub"));
        Assert.True(duringResolve.Count(c => c.Arguments.Contains("list")) <= 2,
            $"expected at most 2 listings of the parent, got {duringResolve.Count(c => c.Arguments.Contains("list"))}");
    }

    [Fact]
    public async Task RunAsync_AfterTheConflictIsResolved_StopsReportingIt()
    {
        var (pair, conflict, sut, cli, store) = await ParkedConflictAsync();
        await sut.ResolveConflictAsync(pair, conflict, ConflictResolution.KeepLocal);

        // The remote now matches what we uploaded, so the next run finds no conflict at all — and
        // must not leave the old row lying around either.
        cli.RespondForPath(RemoteRoot, $"[{FileEntry("a.txt", "my local version")}]");
        var plan = await sut.RunAsync(pair);

        Assert.Empty(plan.Conflicts);
        Assert.Empty(await store.GetConflictActionsAsync(pair.Id));
    }

    // ------------------------------------------------------------------ F2: retries (§7)

    [Fact]
    public async Task RunAsync_TransientFailure_SchedulesARetry_InsteadOfFailingTheRowPermanently()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{FileEntry("a.txt", "hello")}]");
        executor.EnqueueOutput(_ => throw new DriveException("download", 1, "", "connection reset", "connection reset", DriveErrorKind.Network));

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider), clock);

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
        executor.EnqueueOutput(_ => throw new DriveException("download", 1, "", "You need to login first", "You need to login first", DriveErrorKind.NotAuthenticated));

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider), clock);

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

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
        executor.EnqueueOutput(_ => throw new DriveException("download", 1, "", "disk full", "disk full")); // a.txt fails
        executor.EnqueueOutput(args =>
        {
            File.WriteAllText(Path.Combine(args[3], "b.txt"), "world");
            return "";
        });

        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore);
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var sut = new SyncExecutor(provider.Operations, stateStore, new LocalScanner(), new RemoteScanner(provider));

        await sut.RunAsync(pair);

        var updated = await stateStore.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.PartialFailure, updated!.LastStatus);
        Assert.False(File.Exists(Path.Combine(_localRoot, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_localRoot, "b.txt"))); // the other action still ran
    }
}
