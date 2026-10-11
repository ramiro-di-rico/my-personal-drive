using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// The CLI activity console under the explorer. Markup, plus the resize handle's drag gesture
/// (docs/PLAN-UX-ROUND-3.md X7) — pointer capture is a visual-tree concern the view model does not
/// know about; it only receives the resulting height steps.
/// </summary>
public partial class ActivityConsolePanel : UserControl
{
    private Point? _consoleResizeLastPoint;

    public ActivityConsolePanel()
    {
        InitializeComponent();

        // The console's own resize handle (docs/PLAN-UX-ROUND-3.md X7).
        ConsoleResizeHandle.PointerPressed += OnConsoleResizeStarted;
        ConsoleResizeHandle.PointerMoved += OnConsoleResizeMoved;
        ConsoleResizeHandle.PointerReleased += OnConsoleResizeFinished;
    }

    /// <summary>
    /// Dragging the handle changes the console body's height. Pointer capture rather than a
    /// window-level handler: without it the drag stops the moment the pointer leaves the 6px strip,
    /// which is immediately.
    /// </summary>
    private void OnConsoleResizeStarted(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } window || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Measured against the window, not this panel: the drag resizes the panel, so a point
        // relative to it would shift under the pointer on every step.
        _consoleResizeLastPoint = e.GetPosition(window);
        e.Pointer.Capture(ConsoleResizeHandle);
        e.Handled = true;
    }

    private void OnConsoleResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_consoleResizeLastPoint is not { } last || DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        var current = e.GetPosition(window);
        // One step per move rather than an absolute offset from the press: the view model clamps,
        // so an absolute delta would keep accumulating past the limit and the handle would then
        // need that distance dragged back before the console moved again.
        viewModel.Console.ResizeCommandConsole(current.Y - last.Y);
        _consoleResizeLastPoint = current;
        e.Handled = true;
    }

    private void OnConsoleResizeFinished(object? sender, PointerReleasedEventArgs e)
    {
        // The height is persisted here rather than on every move: one write per drag, not one per
        // pointer event (docs/PLAN-UX-ROUND-4.md Y6).
        if (_consoleResizeLastPoint is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Console.CommitCommandConsoleHeight();
        }

        _consoleResizeLastPoint = null;
        e.Pointer.Capture(null);
    }
}
