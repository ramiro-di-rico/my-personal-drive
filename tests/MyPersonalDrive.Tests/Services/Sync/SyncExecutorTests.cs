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

    private async Task<SyncPair> CreatePairAsync(SyncStateStore store, SyncDirection direction = SyncDirection.RemoteToLocal)
        => await store.CreatePairAsync(RemoteRoot, _localRoot, direction, ConflictPolicy.Ask);

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

    [Fact]
    public async Task RunAsync_UnsupportedDirection_ThrowsInsteadOfSilentlyMisbehaving()
    {
        var executor = new FakeCliExecutor();
        var stateStore = new SyncStateStore(_dbPath);
        var pair = await CreatePairAsync(stateStore, SyncDirection.TwoWay);
        var service = new ProtonDriveService(executor);
        var sut = new SyncExecutor(service, stateStore, new LocalScanner(), new RemoteScanner(service));

        await Assert.ThrowsAsync<NotSupportedException>(() => sut.RunAsync(pair));
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
