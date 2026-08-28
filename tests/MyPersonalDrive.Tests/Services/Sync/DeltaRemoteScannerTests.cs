using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// <see cref="DeltaRemoteScanner"/>'s merge logic: given a baseline + a canned
/// <see cref="DeltaFetchResult"/>, it must reconstruct the same shape of complete remote-tree
/// dictionary a full-walk <see cref="RemoteScanner"/> would — <see cref="SyncReconciler"/> needs no
/// changes at all as a result. See docs/PLAN-CLOUD-PROVIDERS.md P8.
/// </summary>
public class DeltaRemoteScannerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-delta-scanner-tests-{Guid.NewGuid():N}.db");
    private static readonly PathMapper Mapper = new("/my-files/Docs", "/home/user/Docs");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private SyncStateStore CreateStateStore() => new(_dbPath);

    private static NodeFingerprint Fingerprint(string relativePath, bool isFolder = false, string? contentHash = "hash", string? nodeId = "id")
        => new(relativePath, isFolder, isFolder ? null : 10, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), nodeId, isFolder ? null : contentHash,
            isFolder || contentHash is null ? null : RemoteHashAlgorithm.QuickXor);

    private static DriveItem Item(string path, string name, bool isFolder = false, string? contentHash = "hash", string? nodeId = "id")
        => new(path, name, isFolder, isFolder ? null : 10, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), NodeId: nodeId, ContentHash: isFolder ? null : contentHash);

    [Fact]
    public async Task ScanAsync_WithNoBaseline_UpsertsEveryChangeReported()
    {
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [new DeltaChange(Item("/my-files/Docs/a.txt", "a.txt"), IsDeleted: false)], NextToken: "cursor-1", WasFullResync: true));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), CreateStateStore());

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline: null, pairId: 1);

        var entry = Assert.Single(result).Value;
        Assert.Equal("a.txt", entry.RelativePath);
    }

    [Fact]
    public async Task ScanAsync_MergesUpsertsOntoTheBaseline_KeepingUntouchedEntries()
    {
        var baseline = new Dictionary<string, SyncBaselineEntry>
        {
            ["untouched.txt"] = new("untouched.txt", false, null, Fingerprint("untouched.txt")),
            ["changed.txt"] = new("changed.txt", false, null, Fingerprint("changed.txt", contentHash: "old-hash")),
        };
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [new DeltaChange(Item("/my-files/Docs/changed.txt", "changed.txt", contentHash: "new-hash"), IsDeleted: false)],
            NextToken: "cursor-2", WasFullResync: false));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), CreateStateStore());

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline, pairId: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal("hash", result["untouched.txt"].ContentHash); // survived from the baseline, untouched
        Assert.Equal("new-hash", result["changed.txt"].ContentHash); // overwritten by the delta
    }

    [Fact]
    public async Task ScanAsync_ADeletedChange_RemovesItFromTheMergedDictionary()
    {
        var baseline = new Dictionary<string, SyncBaselineEntry>
        {
            ["gone.txt"] = new("gone.txt", false, null, Fingerprint("gone.txt")),
        };
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [new DeltaChange(Item("/my-files/Docs/gone.txt", "gone.txt"), IsDeleted: true)], NextToken: "cursor-3", WasFullResync: false));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), CreateStateStore());

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline, pairId: 1);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_AChangeOutsideThePairsSubtree_IsExcluded()
    {
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [
                new DeltaChange(Item("/my-files/Docs/in-scope.txt", "in-scope.txt"), IsDeleted: false),
                new DeltaChange(Item("/my-files/OtherPair/out-of-scope.txt", "out-of-scope.txt"), IsDeleted: false),
            ],
            NextToken: "cursor-4", WasFullResync: true));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), CreateStateStore());

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline: null, pairId: 1);

        Assert.Equal(["in-scope.txt"], result.Keys);
    }

    [Fact]
    public async Task ScanAsync_AFullResync_ReplacesTheBaselineDerivedStateInsteadOfMergingOntoIt()
    {
        // A stale baseline entry that the (expired-and-restarted) delta no longer reports must not
        // survive — a full resync's changes are the entire current tree, not an incremental diff.
        var baseline = new Dictionary<string, SyncBaselineEntry>
        {
            ["stale.txt"] = new("stale.txt", false, null, Fingerprint("stale.txt")),
        };
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [new DeltaChange(Item("/my-files/Docs/current.txt", "current.txt"), IsDeleted: false)], NextToken: "cursor-5", WasFullResync: true));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), CreateStateStore());

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline, pairId: 1);

        Assert.Equal(["current.txt"], result.Keys);
    }

    [Fact]
    public async Task ScanAsync_TwoNamesCollidingOnlyByCase_AreBothDroppedAndReported()
    {
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult(
            [
                new DeltaChange(Item("/my-files/Docs/Photos", "Photos", isFolder: true, contentHash: null), IsDeleted: false),
                new DeltaChange(Item("/my-files/Docs/photos", "photos", isFolder: true, contentHash: null), IsDeleted: false),
            ],
            NextToken: "cursor-6", WasFullResync: true));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource, StringComparison.OrdinalIgnoreCase), CreateStateStore());
        var skips = new List<NodeSkip>();
        scanner.NodeSkipped += (_, skip) => skips.Add(skip);

        var result = await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline: null, pairId: 1);

        Assert.Empty(result);
        Assert.Equal(2, skips.Count);
        Assert.All(skips, skip => Assert.Equal(NodeSkipReason.CaseCollision, skip.Reason));
    }

    [Fact]
    public async Task ScanAsync_PersistsTheReturnedNextTokenForThisPair_AndPassesTheStoredTokenBackIn()
    {
        var stateStore = CreateStateStore();
        await stateStore.SetDeltaTokenAsync(1, "stored-cursor");
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult([], NextToken: "new-cursor", WasFullResync: false));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), stateStore);

        await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline: null, pairId: 1);

        Assert.Equal("stored-cursor", deltaSource.ReceivedToken);
        Assert.Equal("new-cursor", await stateStore.GetDeltaTokenAsync(1));
    }

    [Fact]
    public async Task ScanAsync_TwoDifferentPairs_UseIndependentTokens()
    {
        var stateStore = CreateStateStore();
        var deltaSource = new FakeDeltaSource(new DeltaFetchResult([], NextToken: "cursor", WasFullResync: false));
        var scanner = new DeltaRemoteScanner(new FakeCloudDriveProvider(deltaSource), stateStore);

        await stateStore.SetDeltaTokenAsync(1, "pair-1-cursor");
        await stateStore.SetDeltaTokenAsync(2, "pair-2-cursor");
        await scanner.ScanAsync("/my-files/Docs", Mapper, new ExclusionMatcher(), baseline: null, pairId: 2);

        Assert.Equal("pair-2-cursor", deltaSource.ReceivedToken);
        Assert.Equal("pair-1-cursor", await stateStore.GetDeltaTokenAsync(1)); // untouched by pair 2's scan
    }

    private sealed class FakeDeltaSource(DeltaFetchResult result) : IDeltaSource
    {
        public string? ReceivedToken { get; private set; }

        public Task<DeltaFetchResult> GetChangesAsync(string? deltaToken, CancellationToken cancellationToken = default)
        {
            ReceivedToken = deltaToken;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCloudDriveProvider(IDeltaSource? deltaSource, StringComparison comparison = StringComparison.Ordinal) : ICloudDriveProvider
    {
        public ProviderId Id => ProviderId.OneDrive;
        public string DisplayName => "Fake";
        public ProviderCapabilities Capabilities { get; } = new(
            RemoteHash: RemoteHashAlgorithm.QuickXor, SupportsServerSideMove: true, SupportsServerSideCopy: true,
            CopyIsAsynchronous: false, SupportsBatchMove: false, SupportsDelta: true, RequiresRemoteViewInvalidation: false,
            MaxSingleShotUploadBytes: null, UploadChunkSizeBytes: null, MaxRecommendedConcurrency: 4, CanSetRemoteModificationTime: true);
        public IDriveOperations Operations => throw new NotSupportedException();
        public IDriveAuthenticator Auth => throw new NotSupportedException();
        public IProviderPathSyntax Paths { get; } = new FakePathSyntax(comparison);
        public IRemoteViewInvalidator? RemoteView => null;
        public IProviderDiagnostics? Diagnostics => null;
        public IDeltaSource? DeltaSource => deltaSource;
        public event EventHandler<ProviderActivity>? Activity;
        public event EventHandler<string>? ListingParseWarning;

        private sealed class FakePathSyntax(StringComparison comparison) : IProviderPathSyntax
        {
            public StringComparison Comparison => comparison;
            public string Combine(string parentPath, string name) => string.IsNullOrEmpty(parentPath) ? $"/{name}" : $"{parentPath}/{name}";
            public bool IsRemoteNameMappableLocally(string name) => !name.Contains('/');
            public bool IsLocalNameMappableRemotely(string name) => true;
        }
    }
}
