using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md M3. Two behaviors here are the whole point of the class: it must
/// aggregate across the *whole* subtree (a shallow total is what M2 already gives for free), and a
/// cancelled scan must hand back what it has, marked incomplete, rather than throwing away minutes
/// of the user's waiting.
///
/// Field shapes below follow docs/PLAN-LOCAL-SYNC.md Appendix A, verified against a real account.
/// </summary>
public class FolderStatsScannerTests
{
    /// <summary>
    /// Records synchronously. <see cref="Progress{T}"/> posts to the captured context (the thread
    /// pool, in a test), so asserting on what it collected races the scan finishing.
    /// </summary>
    private sealed class RecordingProgress : IProgress<FolderScanProgress>
    {
        public List<FolderScanProgress> Reports { get; } = [];

        public void Report(FolderScanProgress value) => Reports.Add(value);
    }

    private static string FileJson(string uid, string name, long claimedSize, string modifiedAt = "2026-01-01T00:00:00.000Z")
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "{{modifiedAt}}",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{claimedSize}},
                  "claimedModificationTime": "{{modifiedAt}}",
                  "claimedDigests": { "sha1": "hash-{{uid}}" }
                }
              }
            }
            """;

    private static string FolderJson(string uid, string name)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "folder", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;

    private static FolderStatsScanner Build(FakeCliExecutor executor)
        => new(new ProtonDriveService(executor), new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task ItAggregatesTheWholeSubtree_NotJustTheDirectChildren()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Fotos", $"[{FolderJson("u-sub", "2026")}, {FileJson("u-a", "a.jpg", 1000)}]");
        executor.RespondForPath("/my-files/Fotos/2026", $"[{FileJson("u-b", "b.jpg", 2000)}, {FileJson("u-c", "c.webm", 5000)}]");

        var metrics = await Build(executor).ScanAsync("/my-files/Fotos");

        Assert.True(metrics.IsDeep);
        Assert.True(metrics.IsComplete);
        Assert.Equal(8000, metrics.TotalSize);
        Assert.Equal(3, metrics.FileCount);
        Assert.Equal(1, metrics.FolderCount);
        Assert.Equal(2, metrics.ScannedFolderCount);
    }

    [Fact]
    public async Task ItDoesNotResetTheCliCache_UnlikeTheSyncScanner()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Fotos", "[]");

        await Build(executor).ScanAsync("/my-files/Fotos");

        // The sync engine needs a fresh view because a missed node reads as a deletion; a byte
        // count does not, and a reset would make the user's next navigation pay a cold start.
        Assert.Equal(0, executor.RemoteCacheResets);
    }

    [Fact]
    public async Task Buckets_SpanTheSubtree_AndAreOrderedBySize()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Fotos", $"[{FolderJson("u-sub", "raw")}, {FileJson("u-a", "a.jpg", 100)}]");
        executor.RespondForPath("/my-files/Fotos/raw", $"[{FileJson("u-b", "b.webm", 9000)}]");

        var metrics = await Build(executor).ScanAsync("/my-files/Fotos");

        Assert.Equal(FileKind.Video, metrics.Buckets[0].Kind);
        Assert.Equal(9000, metrics.Buckets[0].TotalSize);
        Assert.Contains(metrics.Buckets, bucket => bucket.Kind == FileKind.Image);
    }

    [Fact]
    public async Task LargestItems_AreTheBiggestInTheSubtree_Capped()
    {
        var executor = new FakeCliExecutor();
        var children = Enumerable.Range(1, 8).Select(i => FileJson($"u{i}", $"f{i}.bin", i * 100));
        executor.RespondForPath("/my-files/Fotos", $"[{string.Join(", ", children)}]");

        var metrics = await Build(executor).ScanAsync("/my-files/Fotos");

        Assert.Equal(FolderMetricsCalculator.LargestItemCount, metrics.LargestItems.Count);
        Assert.Equal("f8.bin", metrics.LargestItems[0].Name);
        Assert.Equal("f4.bin", metrics.LargestItems[^1].Name);
    }

    [Fact]
    public async Task ProgressIsReported_AsCountsPerWave()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Fotos", $"[{FolderJson("u-sub", "2026")}]");
        executor.RespondForPath("/my-files/Fotos/2026", "[]");
        var progress = new RecordingProgress();

        await Build(executor).ScanAsync("/my-files/Fotos", progress);

        var reports = progress.Reports;

        // Progress on a BFS cannot be a percentage - the denominator isn't known until the end.
        Assert.NotEmpty(reports);
        Assert.Equal(2, reports[^1].FoldersScanned);
        Assert.Equal(0, reports[^1].FoldersQueued);
    }

    [Fact]
    public async Task ACancelledScan_ReturnsPartialResults_MarkedIncomplete()
    {
        var executor = new FakeCliExecutor();
        using var cts = new CancellationTokenSource();
        // Cancelled from inside the first CLI call rather than from the progress callback:
        // Progress<T> reports through the thread pool, so a cancel raised there races the walk and
        // makes the test tell you about timing instead of about behavior.
        executor.EnqueueOutput(_ =>
        {
            cts.Cancel();
            return $"[{FolderJson("u-sub", "deep")}, {FileJson("u-a", "a.jpg", 1234)}]";
        });
        // No response is configured for /my-files/Fotos/deep: if the scan carried on past the
        // cancellation it would throw instead of returning, which is exactly what this asserts.

        var metrics = await Build(executor).ScanAsync("/my-files/Fotos", progress: null, cts.Token);

        Assert.False(metrics.IsComplete);
        Assert.True(metrics.IsDeep);
        Assert.Equal(1234, metrics.TotalSize);
    }

    [Fact]
    public async Task AnEmptyFolder_ScansCleanly()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Vacía", "[]");

        var metrics = await Build(executor).ScanAsync("/my-files/Vacía");

        Assert.True(metrics.IsComplete);
        Assert.True(metrics.IsEmpty);
        Assert.Equal(1, metrics.ScannedFolderCount);
    }

    [Fact]
    public async Task FilesWithNoRevision_AreCountedAsUnknownSize()
    {
        var executor = new FakeCliExecutor();
        var noRevision = """
            {
              "uid": "u-x", "parentUid": "parent",
              "name": { "ok": true, "value": "pending.bin" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;
        executor.RespondForPath("/my-files/Fotos", $"[{noRevision}, {FileJson("u-a", "a.jpg", 500)}]");

        var metrics = await Build(executor).ScanAsync("/my-files/Fotos");

        Assert.Equal(500, metrics.TotalSize);
        Assert.Equal(1, metrics.UnknownSizeCount);
    }
}
