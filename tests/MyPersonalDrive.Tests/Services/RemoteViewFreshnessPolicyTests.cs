using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The browser's half of Appendix A #16. The CLI serves listings from a cache it never revalidates,
/// so these tests pin the two things that keeps honest: browsing eventually re-asks the server on
/// its own, and the user's Refresh always does.
/// </summary>
public class RemoteViewFreshnessPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstListingOfASession_AlwaysRefreshes()
    {
        // Nothing is known about how old the cache on disk is — it may be days stale from a previous
        // run, which is exactly how the 17-vs-21 discrepancy was found.
        var sut = new RemoteViewFreshnessPolicy();

        Assert.True(sut.ShouldRefresh(T0, force: false));
    }

    [Fact]
    public void NavigatingWithinTheWindow_KeepsTheCache()
    {
        // Clicking through folders must not pay a cold start per click.
        var sut = new RemoteViewFreshnessPolicy(TimeSpan.FromMinutes(2));
        sut.ShouldRefresh(T0, force: false);

        Assert.False(sut.ShouldRefresh(T0.AddSeconds(30), force: false));
        Assert.False(sut.ShouldRefresh(T0.AddSeconds(119), force: false));
    }

    [Fact]
    public void OnceTheWindowPasses_NavigationRefreshesOnItsOwn()
    {
        // The property that stops a stale folder from being wrong forever with no user action.
        var sut = new RemoteViewFreshnessPolicy(TimeSpan.FromMinutes(2));
        sut.ShouldRefresh(T0, force: false);

        Assert.True(sut.ShouldRefresh(T0.AddMinutes(2), force: false));
    }

    [Fact]
    public void Refresh_AlwaysRefreshes_EvenInsideTheWindow()
    {
        // A Refresh button that can hand back the same cached answer is a Refresh button that lies.
        var sut = new RemoteViewFreshnessPolicy(TimeSpan.FromMinutes(2));
        sut.ShouldRefresh(T0, force: false);

        Assert.True(sut.ShouldRefresh(T0.AddSeconds(1), force: true));
    }

    [Fact]
    public void AForcedRefresh_RestartsTheWindow()
    {
        // Otherwise a Refresh would be followed by an immediate second discard on the next click,
        // paying two cold starts for one user action.
        var sut = new RemoteViewFreshnessPolicy(TimeSpan.FromMinutes(2));
        sut.ShouldRefresh(T0, force: true);

        Assert.False(sut.ShouldRefresh(T0.AddSeconds(30), force: false));
    }
}
