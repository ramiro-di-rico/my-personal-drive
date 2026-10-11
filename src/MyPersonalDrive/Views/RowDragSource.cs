using Avalonia;
using Avalonia.Input;

namespace MyPersonalDrive.Views;

/// <summary>
/// The press-then-move half of dragging a row out of a pane: a press arms it, and the first move
/// past <see cref="RowGestures.DragStartThresholdPixels"/> with the button still down starts the
/// drag. Below that threshold nothing happens, so PointerReleased still reaches the row's own
/// Button and the click that navigates into a folder is not eaten.
/// </summary>
/// <remarks>
/// What is dragged is the pane's to say (<paramref name="createTransfer"/>); this class only knows
/// when. <see cref="OnPointerMoved"/> is the pointer handler itself, attached on Tunnel by the
/// pane — it is <c>async void</c>, so it contains every exception and hands it to
/// <paramref name="onError"/>.
/// </remarks>
internal sealed class RowDragSource<TNode>(Func<TNode, DataTransfer> createTransfer, Action<Exception> onError)
    where TNode : class
{
    private Point? _startPoint;
    private TNode? _candidate;
    private PointerPressedEventArgs? _pressedArgs;

    /// <summary>Remembers where and on what the press landed; the drag starts on a later move.</summary>
    public void Arm(PointerPressedEventArgs e, TNode node)
    {
        _startPoint = e.GetPosition(null);
        _candidate = node;
        _pressedArgs = e;
    }

    public async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_startPoint is not { } start || _candidate is not { } node || _pressedArgs is not { } pressedArgs)
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            Disarm();
            return;
        }

        var current = e.GetPosition(null);
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < RowGestures.DragStartThresholdPixels)
        {
            return;
        }

        Disarm();

        try
        {
            await DragDrop.DoDragDropAsync(pressedArgs, createTransfer(node), DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // This is async void — an exception escaping here would otherwise crash the process.
            onError(ex);
        }
    }

    private void Disarm()
    {
        _startPoint = null;
        _candidate = null;
        _pressedArgs = null;
    }
}
