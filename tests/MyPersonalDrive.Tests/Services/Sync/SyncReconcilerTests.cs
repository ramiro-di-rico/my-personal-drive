using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// One test per row of the decision table in docs/PLAN-LOCAL-SYNC.md §5.2, per direction mode,
/// per docs/PLAN-LOCAL-SYNC.md §10 ("non-negotiable"). Pure function, no IO: every test builds
/// three in-memory dictionaries and asserts on the resulting SyncPlan.
/// </summary>
public class SyncReconcilerTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-31T18:00:00Z");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

    private static NodeFingerprint FileFp(string path, long size = 100, string? hash = "hash-a", DateTimeOffset? modifiedAt = null)
        => new(path, false, size, modifiedAt ?? T0, null, hash);

    private static NodeFingerprint FolderFp(string path)
        => new(path, true, null, null, null, null);

    private static SyncBaselineEntry BaselineOf(string path, bool isFolder, NodeFingerprint? localAtSync, NodeFingerprint? remoteAtSync)
        => new(path, isFolder, localAtSync, remoteAtSync);

    private static Dictionary<string, NodeFingerprint> Map(params NodeFingerprint[] items)
        => items.ToDictionary(i => i.RelativePath);

    private static Dictionary<string, SyncBaselineEntry> BaselineMap(params SyncBaselineEntry[] items)
        => items.ToDictionary(i => i.RelativePath);

    private static readonly Dictionary<string, NodeFingerprint> Empty = new();
    private static readonly Dictionary<string, SyncBaselineEntry> NoBaseline = new();

    private static SyncPlan Reconcile(
        SyncDirection direction,
        Dictionary<string, NodeFingerprint>? local = null,
        Dictionary<string, NodeFingerprint>? remote = null,
        Dictionary<string, SyncBaselineEntry>? baseline = null,
        ConflictPolicy policy = ConflictPolicy.Ask)
        => SyncReconciler.Reconcile(pairId: 1, direction, policy, local ?? Empty, remote ?? Empty, baseline ?? NoBaseline, Timestamp);

    // ---- TwoWay: row by row from §5.2 ----

    [Fact]
    public void TwoWay_BothNew_NoBaseline_Identical_UpdatesBaselineOnly()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.TwoWay, Map(fp), Map(fp with { }));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UpdateBaselineOnly, action.Operation);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void TwoWay_BothNew_NoBaseline_Differing_IsConflict()
    {
        var plan = Reconcile(SyncDirection.TwoWay,
            Map(FileFp("a.txt", hash: "hash-local")),
            Map(FileFp("a.txt", hash: "hash-remote")));

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictReason.BothAppearedDiffering, conflict.Reason);
        Assert.Empty(plan.Actions); // default policy Ask -> no auto action
    }

    [Fact]
    public void TwoWay_NewLocalFile_NoBaseline_Uploads()
    {
        var plan = Reconcile(SyncDirection.TwoWay, local: Map(FileFp("a.txt")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, action.Operation);
    }

    [Fact]
    public void TwoWay_NewLocalFolder_NoBaseline_CreatesRemoteFolder()
    {
        var plan = Reconcile(SyncDirection.TwoWay, local: Map(FolderFp("Photos")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.CreateRemoteFolder, action.Operation);
    }

    [Fact]
    public void TwoWay_NewRemoteFile_NoBaseline_Downloads()
    {
        var plan = Reconcile(SyncDirection.TwoWay, remote: Map(FileFp("a.txt")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.DownloadFile, action.Operation);
    }

    [Fact]
    public void TwoWay_NewRemoteFolder_NoBaseline_CreatesLocalFolder()
    {
        var plan = Reconcile(SyncDirection.TwoWay, remote: Map(FolderFp("Photos")));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.CreateLocalFolder, action.Operation);
    }

    [Fact]
    public void TwoWay_BothPresent_Unchanged_NoAction()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.TwoWay, Map(fp), Map(fp), BaselineMap(BaselineOf("a.txt", false, fp, fp)));

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void TwoWay_LocalChangedOnly_Uploads()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var localFp = FileFp("a.txt", hash: "hash-new");
        var plan = Reconcile(SyncDirection.TwoWay, Map(localFp), Map(baselineFp), BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, action.Operation);
    }

    [Fact]
    public void TwoWay_RemoteChangedOnly_Downloads()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var remoteFp = FileFp("a.txt", hash: "hash-new");
        var plan = Reconcile(SyncDirection.TwoWay, Map(baselineFp), Map(remoteFp), BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.DownloadFile, action.Operation);
    }

    [Fact]
    public void TwoWay_BothChanged_Ask_ConflictWithNoAction()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var localFp = FileFp("a.txt", hash: "hash-local");
        var remoteFp = FileFp("a.txt", hash: "hash-remote");
        var plan = Reconcile(SyncDirection.TwoWay, Map(localFp), Map(remoteFp), BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)), ConflictPolicy.Ask);

        Assert.Empty(plan.Actions);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictReason.BothChanged, conflict.Reason);
    }

    [Fact]
    public void TwoWay_BothChanged_PreferLocal_Uploads()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var plan = Reconcile(SyncDirection.TwoWay,
            Map(FileFp("a.txt", hash: "hash-local")), Map(FileFp("a.txt", hash: "hash-remote")),
            BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)), ConflictPolicy.PreferLocal);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, action.Operation);
        Assert.Single(plan.Conflicts); // still recorded, just auto-resolved
    }

    [Fact]
    public void TwoWay_BothChanged_PreferRemote_Downloads()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var plan = Reconcile(SyncDirection.TwoWay,
            Map(FileFp("a.txt", hash: "hash-local")), Map(FileFp("a.txt", hash: "hash-remote")),
            BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)), ConflictPolicy.PreferRemote);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.DownloadFile, action.Operation);
    }

    [Fact]
    public void TwoWay_BothChanged_KeepBoth_ProducesRenamedSecondaryPath()
    {
        var baselineFp = FileFp("docs/a.txt", hash: "hash-old");
        var plan = Reconcile(SyncDirection.TwoWay,
            Map(FileFp("docs/a.txt", hash: "hash-local")), Map(FileFp("docs/a.txt", hash: "hash-remote")),
            BaselineMap(BaselineOf("docs/a.txt", false, baselineFp, baselineFp)), ConflictPolicy.KeepBoth);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.ResolveConflictKeepBoth, action.Operation);
        Assert.Equal("docs/a.txt", action.RelativePath);
        Assert.Equal("docs/a (local conflict 2026-07-31 18-00-00).txt", action.SecondaryPath);
    }

    [Fact]
    public void TwoWay_DeletedRemotely_LocalUnchanged_DeletesLocal()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.TwoWay, Map(fp), baseline: BaselineMap(BaselineOf("a.txt", false, fp, fp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.DeleteLocal, action.Operation);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void TwoWay_DeletedRemotely_LocalChanged_ReUploadsAndFlagsConflict()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var localFp = FileFp("a.txt", hash: "hash-new");
        var plan = Reconcile(SyncDirection.TwoWay, Map(localFp), baseline: BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, action.Operation);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictReason.RemoteDeletedLocalChanged, conflict.Reason);
    }

    [Fact]
    public void TwoWay_DeletedRemotely_LocalChanged_ReUploads_RegardlessOfPolicy()
    {
        // This branch is auto-resolved, not gated by ConflictPolicy (see §5.2) — verify with a
        // policy that would otherwise mean "do nothing" (Ask).
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var localFp = FileFp("a.txt", hash: "hash-new");
        var plan = Reconcile(SyncDirection.TwoWay, Map(localFp), baseline: BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)), policy: ConflictPolicy.Ask);

        Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, plan.Actions[0].Operation);
    }

    [Fact]
    public void TwoWay_DeletedLocally_RemoteUnchanged_TrashesRemote()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.TwoWay, remote: Map(fp), baseline: BaselineMap(BaselineOf("a.txt", false, fp, fp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.TrashRemote, action.Operation);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void TwoWay_DeletedLocally_RemoteChanged_ReDownloadsAndFlagsConflict()
    {
        var baselineFp = FileFp("a.txt", hash: "hash-old");
        var remoteFp = FileFp("a.txt", hash: "hash-new");
        var plan = Reconcile(SyncDirection.TwoWay, remote: Map(remoteFp), baseline: BaselineMap(BaselineOf("a.txt", false, baselineFp, baselineFp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.DownloadFile, action.Operation);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictReason.LocalDeletedRemoteChanged, conflict.Reason);
    }

    [Fact]
    public void TwoWay_DeletedOnBothSides_ClearsBaseline()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.TwoWay, baseline: BaselineMap(BaselineOf("a.txt", false, fp, fp)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.ClearBaseline, action.Operation);
    }

    [Fact]
    public void TwoWay_Folders_AreNeverContentConflicted_EvenWithDivergentTimestamps()
    {
        // Folders carry no hash and their mtime shifts whenever a child changes — the
        // reconciler must not treat that as a "both changed" conflict.
        var baselineFolder = new SyncBaselineEntry("Photos", true,
            new NodeFingerprint("Photos", true, null, T0, null, null),
            new NodeFingerprint("Photos", true, null, T0, null, null));
        var localFolder = new NodeFingerprint("Photos", true, null, T1, null, null);
        var remoteFolder = new NodeFingerprint("Photos", true, null, T1.AddDays(1), null, null);

        var plan = Reconcile(SyncDirection.TwoWay, Map(localFolder), Map(remoteFolder), BaselineMap(baselineFolder));

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void TwoWay_MtimeWithinTolerance_NoHash_IsNotAChange()
    {
        var baseline = FileFp("a.txt", hash: null, modifiedAt: T0);
        var localOnly = FileFp("a.txt", hash: null, modifiedAt: T0.AddSeconds(1));

        var plan = Reconcile(SyncDirection.TwoWay, Map(localOnly), Map(baseline), BaselineMap(BaselineOf("a.txt", false, baseline, baseline)));

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void TwoWay_MtimeBeyondTolerance_NoHash_IsAChange()
    {
        var baseline = FileFp("a.txt", hash: null, modifiedAt: T0);
        var localChanged = FileFp("a.txt", hash: null, modifiedAt: T0.AddSeconds(10));

        var plan = Reconcile(SyncDirection.TwoWay, Map(localChanged), Map(baseline), BaselineMap(BaselineOf("a.txt", false, baseline, baseline)));

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncOperation.UploadFile, action.Operation);
    }

    // ---- One-way: RemoteToLocal ----

    [Fact]
    public void RemoteToLocal_NewRemoteFile_Downloads()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, remote: Map(FileFp("a.txt")));

        Assert.Equal(SyncOperation.DownloadFile, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void RemoteToLocal_NewRemoteFolder_CreatesLocalFolder()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, remote: Map(FolderFp("Photos")));

        Assert.Equal(SyncOperation.CreateLocalFolder, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void RemoteToLocal_RemovedFromRemote_DeletesLocal()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, local: Map(FileFp("a.txt")));

        Assert.Equal(SyncOperation.DeleteLocal, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void RemoteToLocal_Unchanged_NoAction()
    {
        var fp = FileFp("a.txt");
        var plan = Reconcile(SyncDirection.RemoteToLocal, Map(fp), Map(fp));

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void RemoteToLocal_RemoteChanged_OverwritesLocal_NoBaselineNeeded()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal,
            local: Map(FileFp("a.txt", hash: "hash-old-local")),
            remote: Map(FileFp("a.txt", hash: "hash-new-remote")));

        Assert.Equal(SyncOperation.DownloadFile, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void RemoteToLocal_NeitherSideHasIt_NoAction()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void RemoteToLocal_NeverTouchesRemote()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, local: Map(FileFp("orphan.txt")));

        Assert.DoesNotContain(plan.Actions, a => a.Operation is SyncOperation.UploadFile or SyncOperation.TrashRemote or SyncOperation.CreateRemoteFolder);
    }

    // ---- One-way: LocalToRemote (mirror image) ----

    [Fact]
    public void LocalToRemote_NewLocalFile_Uploads()
    {
        var plan = Reconcile(SyncDirection.LocalToRemote, local: Map(FileFp("a.txt")));

        Assert.Equal(SyncOperation.UploadFile, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void LocalToRemote_RemovedLocally_TrashesRemote()
    {
        var plan = Reconcile(SyncDirection.LocalToRemote, remote: Map(FileFp("a.txt")));

        Assert.Equal(SyncOperation.TrashRemote, Assert.Single(plan.Actions).Operation);
    }

    [Fact]
    public void LocalToRemote_NeverTouchesLocal()
    {
        var plan = Reconcile(SyncDirection.LocalToRemote, remote: Map(FileFp("orphan.txt")));

        Assert.DoesNotContain(plan.Actions, a => a.Operation is SyncOperation.DownloadFile or SyncOperation.DeleteLocal or SyncOperation.CreateLocalFolder);
    }

    // ---- Execution order (§5.3) ----

    [Fact]
    public void Plan_OrdersCreatesBeforeTransfersBeforeDeletesBeforeBaseline()
    {
        var plan = SyncReconciler.Reconcile(1, SyncDirection.TwoWay, ConflictPolicy.Ask,
            local: Map(FileFp("new.txt"), FileFp("stale.txt")),
            remote: Map(FolderFp("NewFolder")),
            baseline: BaselineMap(
                BaselineOf("stale.txt", false, FileFp("stale.txt"), FileFp("stale.txt")),
                BaselineOf("gone-both.txt", false, FileFp("gone-both.txt"), FileFp("gone-both.txt"))),
            conflictTimestamp: Timestamp);

        var operations = plan.Actions.Select(a => a.Operation).ToList();
        var createIndex = operations.IndexOf(SyncOperation.CreateLocalFolder);
        var transferIndex = operations.IndexOf(SyncOperation.UploadFile);
        var deleteIndex = operations.IndexOf(SyncOperation.DeleteLocal);
        var baselineIndex = operations.IndexOf(SyncOperation.ClearBaseline);

        Assert.True(createIndex < transferIndex);
        Assert.True(transferIndex < deleteIndex);
        Assert.True(deleteIndex < baselineIndex);
    }

    [Fact]
    public void Plan_OrdersFolderCreation_ShallowestFirst()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, remote: Map(
            FolderFp("a/b/c"),
            FolderFp("a"),
            FolderFp("a/b")));

        Assert.Equal(["a", "a/b", "a/b/c"], plan.Actions.Select(a => a.RelativePath));
    }

    [Fact]
    public void Plan_OrdersDeletes_DeepestFirst()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, local: Map(
            FileFp("a/b/c.txt"),
            FileFp("a/b.txt")));

        Assert.Equal(["a/b/c.txt", "a/b.txt"], plan.Actions.Select(a => a.RelativePath));
    }

    // ---- Stats ----

    [Fact]
    public void Stats_CountsFilesFoldersAndBytes()
    {
        var plan = Reconcile(SyncDirection.RemoteToLocal, remote: Map(
            FileFp("a.txt", size: 100),
            FileFp("b.txt", size: 200),
            FolderFp("Photos")));

        Assert.Equal(2, plan.Stats.FilesToDownload);
        Assert.Equal(1, plan.Stats.FoldersToCreateLocally);
        Assert.Equal(300, plan.Stats.BytesToDownload);
        Assert.Equal(0, plan.Stats.Conflicts);
    }

    [Fact]
    public void Stats_CountsConflicts()
    {
        var plan = Reconcile(SyncDirection.TwoWay,
            Map(FileFp("a.txt", hash: "hash-local")),
            Map(FileFp("a.txt", hash: "hash-remote")));

        Assert.Equal(1, plan.Stats.Conflicts);
    }
}
