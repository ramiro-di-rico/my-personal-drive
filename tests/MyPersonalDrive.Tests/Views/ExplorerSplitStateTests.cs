using Avalonia.Controls;
using MyPersonalDrive.Views;
using Xunit;

namespace MyPersonalDrive.Tests.Views;

/// <summary>
/// Reported live (docs/PLAN-UX-ROUND-2.md §15): hiding the local pane and showing it again brought
/// it back as a sliver — often invisible — until the splitter was dragged. The collapse restored
/// only the local column, leaving the remote one at whatever absolute width the last splitter drag
/// had written.
/// </summary>
public class ExplorerSplitStateTests
{
    private static readonly GridLength Even = new(1, GridUnitType.Star);

    [Fact]
    public void Collapsing_GivesTheWholeRowToTheRemotePane()
    {
        var sut = new ExplorerSplitState();

        var (remote, local) = sut.Collapse(Even, Even);

        Assert.Equal(Even, remote);
        Assert.True(ExplorerSplitState.IsCollapsed(local));
    }

    /// <summary>
    /// The exact sequence that reproduced it: drag the splitter (which writes absolute widths to
    /// both adjacent columns), hide the local pane, show it again.
    /// </summary>
    [Fact]
    public void AfterADrag_HidingAndShowing_RestoresTheDraggedSplit()
    {
        var sut = new ExplorerSplitState();
        var draggedRemote = new GridLength(900, GridUnitType.Pixel);
        var draggedLocal = new GridLength(380, GridUnitType.Pixel);

        var collapsed = sut.Collapse(draggedRemote, draggedLocal);
        Assert.True(ExplorerSplitState.IsCollapsed(collapsed.Local));
        // While collapsed the remote pane must fill the row, not sit at its dragged width with a
        // gap beside it.
        Assert.Equal(Even, collapsed.Remote);

        var (remote, local) = sut.Restore();

        Assert.Equal(draggedRemote, remote);
        Assert.Equal(draggedLocal, local);
    }

    // The bug in one assertion: restoring the local column alone is not enough, because the remote
    // column is what was left oversized.
    [Fact]
    public void Restoring_PutsTheRemoteColumnBackToo_NotJustTheLocalOne()
    {
        var sut = new ExplorerSplitState();
        sut.Collapse(new GridLength(1200, GridUnitType.Pixel), Even);

        var (remote, _) = sut.Restore();

        Assert.NotEqual(Even, remote);
        Assert.Equal(new GridLength(1200, GridUnitType.Pixel), remote);
    }

    [Fact]
    public void WithNothingEverSaved_RestoringGivesAnEvenSplit()
    {
        var sut = new ExplorerSplitState();

        var (remote, local) = sut.Restore();

        Assert.Equal(Even, remote);
        Assert.Equal(Even, local);
    }

    /// <summary>
    /// The panel state is persisted, so a collapse is re-applied at startup before the user has
    /// touched anything. That second collapse must not record the collapsed widths as the split to
    /// return to — which would make the pane un-showable for the rest of the session.
    /// </summary>
    [Fact]
    public void CollapsingTwice_DoesNotOverwriteTheRememberedSplit()
    {
        var sut = new ExplorerSplitState();
        var draggedRemote = new GridLength(700, GridUnitType.Pixel);
        var draggedLocal = new GridLength(500, GridUnitType.Pixel);

        var first = sut.Collapse(draggedRemote, draggedLocal);
        sut.Collapse(first.Remote, first.Local);

        var (remote, local) = sut.Restore();

        Assert.Equal(draggedRemote, remote);
        Assert.Equal(draggedLocal, local);
        Assert.False(ExplorerSplitState.IsCollapsed(local));
    }

    [Fact]
    public void AStarSizedColumn_IsNeverMistakenForACollapsedOne()
    {
        Assert.False(ExplorerSplitState.IsCollapsed(Even));
        Assert.False(ExplorerSplitState.IsCollapsed(new GridLength(0, GridUnitType.Star)));
        Assert.True(ExplorerSplitState.IsCollapsed(new GridLength(0)));
    }
}
