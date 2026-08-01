using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class ChangeDebouncerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00Z");
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    [Fact]
    public void APathIsHeldBack_UntilItHasBeenQuietForTheWholePeriod()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);

        sut.Record("a.txt");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(sut.TakeSettled());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["a.txt"], sut.TakeSettled());
    }

    [Fact]
    public void RepeatedEventsResetTheClock_SoOneEditorSaveYieldsOneChange()
    {
        // The §6.3 case: a single save emits 3-6 events in quick succession.
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);

        foreach (var _ in Enumerable.Range(0, 5))
        {
            sut.Record("doc.odt");
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Empty(sut.TakeSettled()); // never released mid-burst
        }

        clock.Advance(Quiet);
        Assert.Equal(["doc.odt"], sut.TakeSettled());
    }

    [Fact]
    public void ALongRunningCopy_YieldsNothingUntilItStops()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);

        for (var i = 0; i < 20; i++)
        {
            sut.Record("big.iso");
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Empty(sut.TakeSettled());
        }

        clock.Advance(Quiet);
        Assert.Equal(["big.iso"], sut.TakeSettled());
    }

    [Fact]
    public void SettledPathsAreReleasedOnce_NotRepeatedly()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);
        sut.Record("a.txt");
        clock.Advance(Quiet);

        Assert.Equal(["a.txt"], sut.TakeSettled());
        Assert.Empty(sut.TakeSettled());
        Assert.False(sut.HasPending);
    }

    [Fact]
    public void SettledAndUnsettledPaths_AreSeparatedNotBatchedTogether()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);

        sut.Record("old.txt");
        clock.Advance(Quiet);
        sut.Record("fresh.txt");

        Assert.Equal(["old.txt"], sut.TakeSettled());
        Assert.True(sut.HasPending); // fresh.txt is still waiting

        clock.Advance(Quiet);
        Assert.Equal(["fresh.txt"], sut.TakeSettled());
    }

    [Fact]
    public void Clear_DropsEverything_ForTheBufferOverflowCase()
    {
        var clock = new FakeTimeProvider(T0);
        var sut = new ChangeDebouncer(clock, Quiet);
        sut.Record("a.txt");

        sut.Clear();
        clock.Advance(Quiet);

        Assert.Empty(sut.TakeSettled());
        Assert.False(sut.HasPending);
    }
}
