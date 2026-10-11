using Avalonia.Controls;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// The file viewer panel laid over the listing: text, image and PDF previews. Markup, plus the zoom
/// slider's end-of-gesture hook — the view model persists the zoom, but only the view knows when a
/// drag has finished.
/// </summary>
public partial class FilePreviewPanel : UserControl
{
    public FilePreviewPanel()
    {
        InitializeComponent();

        // One write per gesture, not one per tick — the same reasoning as the console's resize
        // handle (docs/PLAN-UX-ROUND-4.md Y6). PointerCaptureLost is the slider's end-of-drag;
        // LostFocus covers the arrow keys, which change the value without a pointer ever being
        // involved.
        ViewerZoomSlider.PointerCaptureLost += (_, _) => CommitZoom();
        ViewerZoomSlider.LostFocus += (_, _) => CommitZoom();
    }

    private void CommitZoom()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Preview.CommitViewerZoom();
        }
    }
}
