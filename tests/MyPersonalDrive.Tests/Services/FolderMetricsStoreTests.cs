using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md M4. A row here cost the user minutes of scanning, so the rules are
/// asymmetric on purpose: only a *complete deep* scan is ever written, and any change under (or
/// above) a folder throws its number away rather than showing a total that no longer holds.
/// </summary>
public class FolderMetricsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-metrics-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static FolderMetrics Deep(string path, long totalSize = 5000, bool isComplete = true) => new(
        Path: path,
        IsDeep: true,
        IsComplete: isComplete,
        FileCount: 12,
        FolderCount: 3,
        TotalSize: totalSize,
        UnknownSizeCount: 1,
        Buckets: [new FolderKindBucket(FileKind.Video, 2, 4000), new FolderKindBucket(FileKind.Image, 10, 1000)],
        LargestItems: [new DriveItem($"{path}/big.bin", "big.bin", false, 4000)],
        NewestModifiedAt: Now.AddDays(-1),
        OldestModifiedAt: Now.AddYears(-3),
        ScannedFolderCount: 4,
        ComputedAt: Now);

    [Fact]
    public async Task SaveThenGet_RoundTripsTheTotalsAndBuckets()
    {
        var store = new FolderMetricsStore(_dbPath);

        await store.SaveAsync(Deep("/my-files/Fotos"));
        var loaded = await store.GetAsync("/my-files/Fotos");

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsDeep);
        Assert.True(loaded.IsComplete);
        Assert.Equal(5000, loaded.TotalSize);
        Assert.Equal(12, loaded.FileCount);
        Assert.Equal(1, loaded.UnknownSizeCount);
        Assert.Equal(4, loaded.ScannedFolderCount);
        Assert.Equal(Now, loaded.ComputedAt);
        Assert.Equal(FileKind.Video, loaded.Buckets[0].Kind);
        Assert.Equal(4000, loaded.Buckets[0].TotalSize);
        Assert.Equal(Now.AddDays(-1), loaded.NewestModifiedAt);
    }

    [Fact]
    public async Task ALoadedMetric_CarriesNoLargestItems()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));

        var loaded = await store.GetAsync("/my-files/Fotos");

        // Names and sizes of individual files go stale the moment anything in the subtree changes,
        // so they aren't stored; the totals are what a reopened metric can still vouch for.
        Assert.Empty(loaded!.LargestItems);
    }

    [Fact]
    public async Task APartialScan_IsNotStored()
    {
        var store = new FolderMetricsStore(_dbPath);

        await store.SaveAsync(Deep("/my-files/Fotos", isComplete: false));

        Assert.Null(await store.GetAsync("/my-files/Fotos"));
    }

    [Fact]
    public async Task AShallowMetric_IsNotStored()
    {
        var store = new FolderMetricsStore(_dbPath);

        await store.SaveAsync(FolderMetricsCalculator.FromChildren("/my-files/Fotos", [new DriveItem("/my-files/Fotos/a.jpg", "a.jpg", false, 10)], Now));

        Assert.Null(await store.GetAsync("/my-files/Fotos"));
    }

    [Fact]
    public async Task SavingTheSameFolderTwice_Overwrites()
    {
        var store = new FolderMetricsStore(_dbPath);

        await store.SaveAsync(Deep("/my-files/Fotos", totalSize: 1000));
        await store.SaveAsync(Deep("/my-files/Fotos", totalSize: 2000));

        Assert.Equal(2000, (await store.GetAsync("/my-files/Fotos"))!.TotalSize);
    }

    [Fact]
    public async Task GetMany_ReturnsOnlyTheFoldersThatHaveAMetric()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));
        await store.SaveAsync(Deep("/my-files/Libros"));

        var found = await store.GetManyAsync(["/my-files/Fotos", "/my-files/Libros", "/my-files/Temp"]);

        Assert.Equal(2, found.Count);
        Assert.True(found.ContainsKey("/my-files/Fotos"));
        Assert.False(found.ContainsKey("/my-files/Temp"));
    }

    [Fact]
    public async Task GetMany_WithNoPaths_DoesNotQuery()
        => Assert.Empty(await new FolderMetricsStore(_dbPath).GetManyAsync([]));

    [Fact]
    public async Task AChangeInsideAFolder_InvalidatesTheFolderAndItsAncestors()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files"));
        await store.SaveAsync(Deep("/my-files/Fotos"));
        await store.SaveAsync(Deep("/my-files/Fotos/2026"));
        await store.SaveAsync(Deep("/my-files/Libros"));

        await store.InvalidateForChangeAtAsync("/my-files/Fotos/2026/big.jpg");

        Assert.Null(await store.GetAsync("/my-files"));
        Assert.Null(await store.GetAsync("/my-files/Fotos"));
        Assert.Null(await store.GetAsync("/my-files/Fotos/2026"));
        // A sibling subtree is unaffected: nothing about it changed.
        Assert.NotNull(await store.GetAsync("/my-files/Libros"));
    }

    [Fact]
    public async Task TrashingAFolder_AlsoInvalidatesItsDescendants()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));
        await store.SaveAsync(Deep("/my-files/Fotos/2026"));

        await store.InvalidateForChangeAtAsync("/my-files/Fotos");

        Assert.Null(await store.GetAsync("/my-files/Fotos"));
        Assert.Null(await store.GetAsync("/my-files/Fotos/2026"));
    }

    [Fact]
    public async Task AFolderWhoseNameSharesAPrefix_IsNotInvalidated()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));
        await store.SaveAsync(Deep("/my-files/Fotos-viejas"));

        await store.InvalidateForChangeAtAsync("/my-files/Fotos");

        // "/my-files/Fotos-viejas" is a sibling, not a descendant: the LIKE pattern has to include
        // the separator or a rename would silently wipe unrelated folders' metrics.
        Assert.NotNull(await store.GetAsync("/my-files/Fotos-viejas"));
    }

    [Fact]
    public async Task ARowWithCorruptBucketJson_StillReturnsItsTotals()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));

        using (var connection = new SqliteConnection(SqliteOffThread.ConnectionStringFor(_dbPath)))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE FolderMetrics SET BucketsJson = '{ not json' WHERE Path = @Path";
            command.Parameters.AddWithValue("@Path", "/my-files/Fotos");
            command.ExecuteNonQuery();
        }

        var loaded = await store.GetAsync("/my-files/Fotos");

        Assert.NotNull(loaded);
        Assert.Equal(5000, loaded!.TotalSize);
        Assert.Empty(loaded.Buckets);
    }

    [Fact]
    public async Task ARowWithAnUnparseableTimestamp_IsTreatedAsAbsent()
    {
        var store = new FolderMetricsStore(_dbPath);
        await store.SaveAsync(Deep("/my-files/Fotos"));

        using (var connection = new SqliteConnection(SqliteOffThread.ConnectionStringFor(_dbPath)))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE FolderMetrics SET ComputedAt = 'whenever' WHERE Path = @Path";
            command.Parameters.AddWithValue("@Path", "/my-files/Fotos");
            command.ExecuteNonQuery();
        }

        // Without a date the UI can't tell the user how old the number is, and an undated total
        // they can't judge is worse than no total.
        Assert.Null(await store.GetAsync("/my-files/Fotos"));
    }

    [Fact]
    public void TheStore_AppliesTheSharedMigrations_SoItCanBeBuiltOnAFreshFile()
    {
        _ = new FolderMetricsStore(_dbPath);

        using var connection = new SqliteConnection(SqliteOffThread.ConnectionStringFor(_dbPath));
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'FolderMetrics'";
        Assert.Equal("FolderMetrics", command.ExecuteScalar());
    }
}
