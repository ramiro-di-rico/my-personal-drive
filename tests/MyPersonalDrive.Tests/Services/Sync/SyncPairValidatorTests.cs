using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncPairValidatorTests
{
    private static SyncPair Existing(string remotePath, string localPath, SyncDirection direction = SyncDirection.TwoWay, int id = 1) => new(
        Id: id, RemotePath: remotePath, LocalPath: localPath,
        Direction: direction, ConflictPolicy: ConflictPolicy.Ask,
        IsEnabled: true, IsPaused: false, ExcludeGlobs: [],
        LastSyncAt: null, LastStatus: SyncPairStatus.Never, LastError: null);

    private static string? Validate(string remotePath, string localPath, params SyncPair[] existing)
        => SyncPairValidator.Validate(remotePath, localPath, SyncDirection.TwoWay, existing);

    private static string? ValidateUpload(string remotePath, string localPath, params SyncPair[] existing)
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
        => Assert.Contains("absolute path", Validate(remotePath, "/home/user/Docs"));

    [Fact]
    public void AnEmptyLocalPath_IsRejected()
        => Assert.Contains("Choose a local folder", Validate("/my-files/Docs", "  "));

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void TheFilesystemRoot_IsRejected(string localPath)
        => Assert.Contains("home directory or the filesystem root", Validate("/my-files/Docs", localPath));

    [Fact]
    public void TheHomeDirectoryItself_IsRejected_EvenWithATrailingSeparator()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Contains("home directory", Validate("/my-files/Docs", home));
        Assert.Contains("home directory", Validate("/my-files/Docs", home + "/"));
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
        Assert.Contains("overlaps", error);
        Assert.Contains("/home/user/Docs", error);
    }

    [Fact]
    public void ALocalFolderContainingAnExistingPair_IsRejectedToo()
        => Assert.Contains("overlaps", Validate("/my-files/Other", "/home/user", Existing("/my-files/Docs", "/home/user/Docs")));

    [Fact]
    public void TheSameLocalFolderTwice_SaysSoPlainly()
    {
        var error = Validate("/my-files/Other", "/home/user/Docs", Existing("/my-files/Docs", "/home/user/Docs"));

        Assert.Contains("already synced", error);
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
        Assert.Contains("remote folder overlaps", error);
        Assert.Contains("undo each other's deletions", error);
    }

    [Fact]
    public void ARemoteFolderContainingAnExistingPair_IsRejectedToo()
        => Assert.Contains("overlaps", Validate("/my-files", "/home/user/Elsewhere", Existing("/my-files/Docs", "/home/user/Docs")));

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

        Assert.Contains("/home/user/B", error);
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

        Assert.Contains("already synced", error);
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

    // ---------------------------------------------------------------- ValidateDirectionChange (editing an existing pair)

    [Fact]
    public void ChangingDirection_ToLocalToRemote_IsAlwaysSafe()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.TwoWay, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        Assert.Null(SyncPairValidator.ValidateDirectionChange(pair, SyncDirection.LocalToRemote, [pair, other]));
    }

    [Fact]
    public void ChangingDirection_AwayFromLocalToRemote_IsRejectedWhenAnotherPairSharesTheFolder()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.LocalToRemote, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        var error = SyncPairValidator.ValidateDirectionChange(pair, SyncDirection.TwoWay, [pair, other]);

        Assert.NotNull(error);
        Assert.Contains("/my-files/B", error);
    }

    [Fact]
    public void ChangingDirection_IsSafeWhenNoOtherPairSharesTheFolder()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.LocalToRemote, id: 1);

        Assert.Null(SyncPairValidator.ValidateDirectionChange(pair, SyncDirection.TwoWay, [pair]));
    }

    [Fact]
    public void ChangingDirection_ToTheSameDirection_IsANoOp()
    {
        var pair = Existing("/my-files/A", "/home/user/Docs", SyncDirection.TwoWay, id: 1);
        var other = Existing("/my-files/B", "/home/user/Docs", SyncDirection.LocalToRemote, id: 2);

        // "Changing" to the direction it already has can't newly break anything, even though the
        // folder is shared and today's actual direction (TwoWay) would fail the create-time check.
        Assert.Null(SyncPairValidator.ValidateDirectionChange(pair, SyncDirection.TwoWay, [pair, other]));
    }
}
