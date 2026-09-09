using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncPairValidatorTests
{
    private static SyncPair Existing(string remotePath, string localPath, SyncDirection direction = SyncDirection.TwoWay, int id = 1, bool mirrorDeletes = true) => new(
        Id: id, RemotePath: remotePath, LocalPath: localPath,
        Direction: direction, ConflictPolicy: ConflictPolicy.Ask,
        IsEnabled: true, IsPaused: false, ExcludeGlobs: [],
        LastSyncAt: null, LastStatus: SyncPairStatus.Never, LastError: null,
        MirrorDeletes: mirrorDeletes);

    /// <summary>A download pair that keeps what it didn't bring — the only download shape allowed to share a folder.</summary>
    private static SyncPair ExistingAdditiveDownload(string remotePath, string localPath, int id = 1)
        => Existing(remotePath, localPath, SyncDirection.RemoteToLocal, id, mirrorDeletes: false);

    private static SyncPairIssue? ValidateAdditiveDownload(string remotePath, string localPath, params SyncPair[] existing)
        => SyncPairValidator.Validate(remotePath, localPath, SyncDirection.RemoteToLocal, existing, existing, mirrorDeletes: false);

    private static SyncPairIssue? ValidateMirroringDownload(string remotePath, string localPath, params SyncPair[] existing)
        => SyncPairValidator.Validate(remotePath, localPath, SyncDirection.RemoteToLocal, existing, existing, mirrorDeletes: true);

    private static SyncPairIssue? Validate(string remotePath, string localPath, params SyncPair[] existing)
        => SyncPairValidator.Validate(remotePath, localPath, SyncDirection.TwoWay, existing);

    private static SyncPairIssue? ValidateUpload(string remotePath, string localPath, params SyncPair[] existing)
        => SyncPairValidator.Validate(remotePath, localPath, SyncDirection.LocalToRemote, existing);

    // ---------------------------------------------------------------- path shape

    [Fact]
    public void AFreshPairWithNoNeighbours_IsAccepted()
        => Assert.Null(Validate("/my-files/Docs", "/home/user/Docs"));

    [Theory]
    [InlineData("my-files/Docs")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARemotePathThatIsNotAbsolute_IsRejected(string remotePath)
        => Assert.Equal(SyncPairIssueKind.RemotePathNotAbsolute, Validate(remotePath, "/home/user/Docs")!.Kind);

    [Fact]
    public void AnEmptyLocalPath_IsRejected()
        => Assert.Equal(SyncPairIssueKind.LocalPathMissing, Validate("/my-files/Docs", "  ")!.Kind);

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void TheFilesystemRoot_IsRejected(string localPath)
        => Assert.Equal(SyncPairIssueKind.LocalPathIsHomeOrRoot, Validate("/my-files/Docs", localPath)!.Kind);

    [Fact]
    public void TheHomeDirectoryItself_IsRejected_EvenWithATrailingSeparator()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(SyncPairIssueKind.LocalPathIsHomeOrRoot, Validate("/my-files/Docs", home)!.Kind);
        Assert.Equal(SyncPairIssueKind.LocalPathIsHomeOrRoot, Validate("/my-files/Docs", home + "/")!.Kind);
    }

    [Fact]
    public void AFolderInsideHome_IsFine()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Null(Validate("/my-files/Docs", Path.Combine(home, "ProtonDrive")));
    }

    // ---------------------------------------------------------------- local overlap (§12)

    [Fact]
    public void ALocalFolderInsideAnExistingPair_IsRejected()
    {
        // The destructive case: the outer pair's scanner walks the inner folder, finds no
        // counterpart under its own remote root, and moves it to the local trash — which the inner
        // pair then downloads again, forever.
        var error = Validate("/my-files/Other", "/home/user/Docs/Sub", Existing("/my-files/Docs", "/home/user/Docs"));

        Assert.NotNull(error);
        Assert.Equal(SyncPairIssueKind.LocalOverlaps, error.Kind);
        Assert.Contains("/home/user/Docs", error.Args.Select(a => a?.ToString()));
    }

    [Fact]
    public void ALocalFolderContainingAnExistingPair_IsRejectedToo()
        => Assert.Equal(SyncPairIssueKind.LocalOverlaps, Validate("/my-files/Other", "/home/user", Existing("/my-files/Docs", "/home/user/Docs"))!.Kind);

    [Fact]
    public void TheSameLocalFolderTwice_IsRefusedForTwoWayPairs_WithTheReasonSharingNeedsOneWay()
    {
        // Sharing one folder is allowed now, but not for a two-way pair on either side — so the
        // answer is the shape requirement, not a flat "already synced".
        var error = Validate("/my-files/Other", "/home/user/Docs", Existing("/my-files/Docs", "/home/user/Docs"));

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsOneWay, error!.Kind);
    }

    [Theory]
    [InlineData("/home/user/Docs/")]
    [InlineData("/home/user/./Docs")]
    [InlineData("/home/user/Downloads/../Docs")]
    public void OverlapIsDetectedThroughDifferentSpellingsOfTheSameFolder(string spelling)
        => Assert.NotNull(Validate("/my-files/Other", spelling, Existing("/my-files/Docs", "/home/user/Docs")));

    [Fact]
    public void ASiblingWhoseNameMerelySharesAPrefix_IsNotOverlap()
    {
        // '/home/user/Docs2' must not read as nested inside '/home/user/Docs'.
        Assert.Null(Validate("/my-files/Other", "/home/user/Docs2", Existing("/my-files/Docs", "/home/user/Docs")));
    }

    // ---------------------------------------------------------------- remote overlap (§12)

    [Fact]
    public void ARemoteFolderInsideAnExistingPair_IsRejected()
    {
        // Echo suppression is keyed per pair, so two pairs over one remote subtree can undo each
        // other's deletions: pair A doesn't know pair B trashed a node, sees it still listed
        // (Appendix A #15), reads it as "new remotely", and downloads it back.
        var error = Validate("/my-files/Docs/Sub", "/home/user/Elsewhere", Existing("/my-files/Docs", "/home/user/Docs"));

        Assert.NotNull(error);
        Assert.Equal(SyncPairIssueKind.RemoteOverlaps, error.Kind);
    }

    [Fact]
    public void ARemoteFolderContainingAnExistingPair_IsRejectedToo()
        => Assert.Equal(SyncPairIssueKind.RemoteOverlaps, Validate("/my-files", "/home/user/Elsewhere", Existing("/my-files/Docs", "/home/user/Docs"))!.Kind);

    [Theory]
    [InlineData("/my-files/Docs/")]
    public void RemoteOverlapIgnoresATrailingSlash(string spelling)
        => Assert.NotNull(Validate(spelling, "/home/user/Elsewhere", Existing("/my-files/Docs", "/home/user/Docs")));

    [Fact]
    public void ARemoteSiblingSharingAPrefix_IsNotOverlap()
        => Assert.Null(Validate("/my-files/Docs2", "/home/user/Elsewhere", Existing("/my-files/Docs", "/home/user/Docs")));

    // ---------------------------------------------------------------- several existing pairs

    [Fact]
    public void OverlapIsCheckedAgainstEveryExistingPair_NotJustTheFirst()
    {
        var error = Validate("/my-files/C", "/home/user/B/Inner",
            Existing("/my-files/A", "/home/user/A"),
            Existing("/my-files/B", "/home/user/B"));

        Assert.Contains("/home/user/B", error.Args.Select(a => a?.ToString()));
    }

    [Fact]
    public void UnrelatedPairs_CoexistFreely()
        => Assert.Null(Validate("/my-files/C", "/home/user/C",
            Existing("/my-files/A", "/home/user/A"),
            Existing("/my-files/B", "/home/user/B")));

    // ---------------------------------------------------------------- fan-out uploads (upload-only sharing a local folder)

    [Fact]
    public void TwoUploadOnlyPairsSharingTheSameLocalFolder_AreBothAccepted()
        => Assert.Null(ValidateUpload("/my-files/Other", "/home/user/Docs",
            Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote)));

    [Fact]
    public void UploadOnlySharing_AlsoWorksWhenTheNewPairIsNestedInsideTheOther()
        => Assert.Null(ValidateUpload("/my-files/Other", "/home/user/Docs/Sub",
            Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote)));

    [Fact]
    public void AnUploadOnlyPair_StillRejectsSharingWithATwoWayPair()
    {
        // The existing pair can write to the folder (download/delete) — sharing would let it
        // destroy what the new upload-only pair sends there.
        var error = ValidateUpload("/my-files/Other", "/home/user/Docs", Existing("/my-files/Docs", "/home/user/Docs"));

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsOneWay, error!.Kind);
    }

    [Fact]
    public void ATwoWayPair_StillRejectsSharingEvenWithAnExistingUploadOnlyPair()
    {
        // The exception only holds when *both* sides are upload-only — a new pair that itself
        // writes locally is unsafe regardless of what the existing pair does.
        var error = Validate("/my-files/Other", "/home/user/Docs", Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote));

        Assert.NotNull(error);
    }

    [Fact]
    public void UploadOnlySharing_IsAllowedAcrossDifferentAccounts()
    {
        // sameAccountPairs is empty (a different account's pair list); allAccountPairs carries the
        // other account's upload-only pair on the same local folder.
        var otherAccountPair = Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote);

        var error = SyncPairValidator.Validate("/onedrive-files/Backup", "/home/user/Docs", SyncDirection.LocalToRemote,
            sameAccountPairs: [], allAccountPairs: [otherAccountPair]);

        Assert.Null(error);
    }

    [Fact]
    public void ATwoWayPair_IsRejectedWhenAnotherAccountAlreadyUploadsFromThatFolder()
    {
        var otherAccountPair = Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote);

        var error = SyncPairValidator.Validate("/onedrive-files/Backup", "/home/user/Docs", SyncDirection.TwoWay,
            sameAccountPairs: [], allAccountPairs: [otherAccountPair]);

        Assert.NotNull(error);
    }

    [Fact]
    public void AnIdenticalRemotePathAcrossDifferentAccounts_IsNotFlagged()
    {
        // Two different providers' remote trees are unrelated storage — the remote-overlap check
        // only ever looks at sameAccountPairs, never allAccountPairs.
        var otherAccountPair = Existing("/my-files/Docs", "/home/user/OtherFolder", SyncDirection.LocalToRemote);

        var error = SyncPairValidator.Validate("/my-files/Docs", "/home/user/NewFolder", SyncDirection.LocalToRemote,
            sameAccountPairs: [], allAccountPairs: [otherAccountPair]);

        Assert.Null(error);
    }

    // ---------------------------------------------------------------- sharing one local folder (§12)

    [Fact]
    public void TwoAdditiveDownloadPairsSharingOneFolder_AreAccepted()
        // The feature: two providers mirroring into one folder, neither deleting what the other
        // brought. Same-named files are handled by the reconciler, not refused here.
        => Assert.Null(ValidateAdditiveDownload("/onedrive/Other", "/home/user/Docs",
            ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs")));

    [Fact]
    public void ADownloadPairThatMirrorsDeletes_CannotShareAFolder()
    {
        // It would trash everything the other provider put there: all of it is missing from this
        // pair's own remote.
        var error = ValidateMirroringDownload("/onedrive/Other", "/home/user/Docs",
            ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs"));

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsAdditive, error!.Kind);
    }

    [Fact]
    public void AnAdditiveDownloadPair_IsRefusedWhenTheExistingPairMirrorsDeletes()
    {
        // The unsafe half is the *existing* pair this time — the rule has to look at both sides.
        var error = ValidateAdditiveDownload("/onedrive/Other", "/home/user/Docs",
            Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.RemoteToLocal, mirrorDeletes: true));

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsAdditive, error!.Kind);
    }

    [Fact]
    public void ADownloadPairAndAnUploadPair_CanShareOneFolder()
    {
        // "Fetch from here, replicate to there." The upload pair never writes locally, so it cannot
        // disturb what the download pair brought.
        Assert.Null(ValidateAdditiveDownload("/onedrive/Other", "/home/user/Docs",
            Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote)));

        Assert.Null(ValidateUpload("/onedrive/Other", "/home/user/Docs",
            ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs")));
    }

    [Fact]
    public void AMirroringDownloadPair_CanStillShareWithAnUploadPair_BecauseNothingElseWritesThere()
    {
        // Mirroring only endangers files another pair *put* in the folder; an upload-only
        // neighbour puts nothing there, so this stays the user's own folder to mirror.
        Assert.Null(ValidateMirroringDownload("/onedrive/Other", "/home/user/Docs",
            Existing("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote)));
    }

    [Fact]
    public void SharingIsAllowedAcrossAccounts_WhichIsThePointOfIt()
    {
        // sameAccountPairs is empty — the other provider's pair only appears in allAccountPairs.
        var otherProvider = ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs");

        Assert.Null(SyncPairValidator.Validate("/onedrive/Backup", "/home/user/Docs", SyncDirection.RemoteToLocal,
            sameAccountPairs: [], allAccountPairs: [otherProvider], mirrorDeletes: false));
    }

    [Fact]
    public void SharingIsOnlyForTheExactSameFolder_NotANestedOne()
    {
        // Nesting is a different failure — the outer pair's scanner walks the inner folder — and
        // being additive does not fix it.
        var error = ValidateAdditiveDownload("/onedrive/Other", "/home/user/Docs/Sub",
            ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs"));

        Assert.Equal(SyncPairIssueKind.LocalOverlaps, error!.Kind);
    }

    [Fact]
    public void EveryPairSharingTheFolderIsChecked_NotJustTheFirstMatch()
    {
        // Two safe neighbours and one unsafe one: the unsafe one must still be found.
        var error = ValidateAdditiveDownload("/onedrive/Third", "/home/user/Docs",
            ExistingAdditiveDownload("/my-files/Docs", "/home/user/Docs", id: 1),
            Existing("/dropbox/Docs", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2),
            Existing("/gdrive/Docs", "/home/user/Docs", SyncDirection.TwoWay, id: 3));

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsOneWay, error!.Kind);
    }

    // ---------------------------------------------------------------- ValidateEdit (editing an existing pair)

    [Fact]
    public void ChangingDirection_ToLocalToRemote_IsAlwaysSafe()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.TwoWay, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        Assert.Null(SyncPairValidator.ValidateEdit(pair, SyncDirection.LocalToRemote, newMirrorDeletes: true, [pair, other]));
    }

    [Fact]
    public void ChangingDirection_AwayFromLocalToRemote_IsRejectedWhenAnotherPairSharesTheFolder()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.LocalToRemote, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        var error = SyncPairValidator.ValidateEdit(pair, SyncDirection.TwoWay, newMirrorDeletes: true, [pair, other]);

        Assert.NotNull(error);
        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsOneWay, error!.Kind);
        Assert.Contains("/my-files/B", error.Args.Select(a => a?.ToString()));
    }

    [Fact]
    public void ChangingDirection_IsSafeWhenNoOtherPairSharesTheFolder()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.LocalToRemote, id: 1);

        Assert.Null(SyncPairValidator.ValidateEdit(pair, SyncDirection.TwoWay, newMirrorDeletes: true, [pair]));
    }

    [Fact]
    public void TurningMirrorDeletesBackOn_IsRejectedOnASharedFolder()
    {
        // The edit that would quietly re-arm the destructive case the create-time rule refused.
        var pair = ExistingAdditiveDownload("/my-files/A", "/home/user/Docs", id: 1);
        var other = ExistingAdditiveDownload("/my-files/B", "/home/user/Docs", id: 2);

        var error = SyncPairValidator.ValidateEdit(pair, SyncDirection.RemoteToLocal, newMirrorDeletes: true, [pair, other]);

        Assert.Equal(SyncPairIssueKind.SharedFolderNeedsAdditive, error!.Kind);
    }

    [Fact]
    public void KeepingMirrorDeletesOff_IsAcceptedOnASharedFolder()
    {
        var pair = ExistingAdditiveDownload("/my-files/A", "/home/user/Docs", id: 1);
        var other = ExistingAdditiveDownload("/my-files/B", "/home/user/Docs", id: 2);

        Assert.Null(SyncPairValidator.ValidateEdit(pair, SyncDirection.RemoteToLocal, newMirrorDeletes: false, [pair, other]));
    }

    [Fact]
    public void AnEditThatChangesNeitherSetting_IsNeverRefused_EvenOnAPairThatPredatesTheRule()
    {
        // A TwoWay pair sharing a folder could not be created today, but one that exists must stay
        // editable — otherwise its conflict policy can never be changed again.
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.TwoWay, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        Assert.Null(SyncPairValidator.ValidateEdit(pair, pair.Direction, pair.MirrorDeletes, [pair, other]));
    }

    [Fact]
    public void ChangingDirection_ToTheSameDirection_IsANoOp()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.TwoWay, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        // "Changing" to the direction it already has can't newly break anything, even though the
        // folder is shared and today's actual direction (TwoWay) would fail the create-time check.
        Assert.Null(SyncPairValidator.ValidateEdit(pair, SyncDirection.TwoWay, newMirrorDeletes: true, [pair, other]));
    }
}
