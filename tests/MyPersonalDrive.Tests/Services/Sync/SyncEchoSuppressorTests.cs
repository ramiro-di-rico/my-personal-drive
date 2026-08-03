using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncEchoSuppressorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00Z");

    private static Dictionary<string, NodeFingerprint> Scan(params string[] paths)
        => paths.ToDictionary(p => p, p => new NodeFingerprint(p, false, 10, T0, null, null), StringComparer.Ordinal);

    private static Dictionary<string, NodeFingerprint> ScanWithFolder(string folder, params string[] children)
    {
        var result = Scan(children);
        result[folder] = new NodeFingerprint(folder, true, null, T0, null, null);
        return result;
    }

    [Fact]
    public void ADeletedPath_IsFilteredOutOfASubsequentScan()
    {
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressDeletion(pairId: 1, SyncSide.Remote, "gone.txt");

        var filtered = sut.Filter(1, SyncSide.Remote, Scan("gone.txt", "kept.txt"));

        Assert.Equal(["kept.txt"], filtered.Keys);
    }

    [Fact]
    public void SuppressionIsScopedToItsPairAndSide()
    {
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressDeletion(pairId: 1, SyncSide.Remote, "gone.txt");

        Assert.Equal(["gone.txt"], sut.Filter(2, SyncSide.Remote, Scan("gone.txt")).Keys);  // other pair
        Assert.Equal(["gone.txt"], sut.Filter(1, SyncSide.Local, Scan("gone.txt")).Keys);   // other side
    }

    [Fact]
    public void SuppressionExpires_SoALegitimateRecreationIsEventuallyBelieved()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new SyncEchoSuppressor(clock, window: TimeSpan.FromSeconds(60));
        sut.SuppressDeletion(1, SyncSide.Remote, "gone.txt");

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(sut.Filter(1, SyncSide.Remote, Scan("gone.txt")));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(["gone.txt"], sut.Filter(1, SyncSide.Remote, Scan("gone.txt")).Keys);
    }

    [Fact]
    public void SuppressionIsReleasedEarly_OnceAScanAgreesTheNodeIsGone()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new SyncEchoSuppressor(clock, window: TimeSpan.FromSeconds(60));
        sut.SuppressDeletion(1, SyncSide.Remote, "gone.txt");

        // The scan has converged: nothing to suppress any more.
        Assert.Equal(["kept.txt"], sut.Filter(1, SyncSide.Remote, Scan("kept.txt")).Keys);

        // So a genuine re-creation moments later is believed, without waiting out the window.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["gone.txt"], sut.Filter(1, SyncSide.Remote, Scan("gone.txt")).Keys);
    }

    [Fact]
    public void DeletingAFolder_AlsoSuppressesItsChildren()
    {
        // A stale listing can report the trashed folder *and* its contents. Suppressing only the
        // exact path would let the children through as "new remotely" — the same bug one level down.
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressDeletion(1, SyncSide.Remote, "Photos");

        var filtered = sut.Filter(1, SyncSide.Remote, ScanWithFolder("Photos", "Photos/a.jpg", "Photos/sub/b.jpg", "PhotosElsewhere.txt"));

        // Note "PhotosElsewhere.txt" survives: prefix matching must respect path boundaries.
        Assert.Equal(["PhotosElsewhere.txt"], filtered.Keys);
    }

    [Fact]
    public void ASuppressedWrite_IsAnEcho_ButIsNeverFilteredOutOfAScan()
    {
        // The distinction that makes the two registers necessary. A file we just downloaded really
        // is on disk, so filtering it out of the local scan would make the reconciler think it was
        // absent — and download it again, or conclude the user deleted it. Only the *event* is noise.
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressWrite(1, SyncSide.Local, "downloaded.txt");

        Assert.True(sut.IsEcho(1, SyncSide.Local, "downloaded.txt"));
        Assert.Equal(["downloaded.txt"], sut.Filter(1, SyncSide.Local, Scan("downloaded.txt")).Keys);
    }

    [Fact]
    public void ASuppressedDeletion_IsAlsoAnEcho_SinceDeletingFiresEventsToo()
    {
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressDeletion(1, SyncSide.Local, "gone.txt");

        Assert.True(sut.IsEcho(1, SyncSide.Local, "gone.txt"));
    }

    [Fact]
    public void WritingAFolder_MakesEventsForItsContentsEchoesToo()
    {
        // A CreateLocalFolder is followed by downloads into it; those events are ours as well.
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        sut.SuppressWrite(1, SyncSide.Local, "Photos");

        Assert.True(sut.IsEcho(1, SyncSide.Local, "Photos/pic.jpg"));
        Assert.False(sut.IsEcho(1, SyncSide.Local, "PhotosElsewhere.txt"));
    }

    [Fact]
    public void AnEchoExpires_SoLaterUserEditsToTheSameFileAreSeen()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new SyncEchoSuppressor(clock, window: TimeSpan.FromSeconds(60));
        sut.SuppressWrite(1, SyncSide.Local, "downloaded.txt");

        Assert.True(sut.IsEcho(1, SyncSide.Local, "downloaded.txt"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(sut.IsEcho(1, SyncSide.Local, "downloaded.txt"));
    }

    [Fact]
    public void WithNothingSuppressed_TheScanIsPassedThroughUntouched()
    {
        var sut = new SyncEchoSuppressor(new FakeTimeProvider(T0));
        var scan = Scan("a.txt", "b.txt");

        Assert.Same(scan, sut.Filter(1, SyncSide.Remote, scan));
    }
}
