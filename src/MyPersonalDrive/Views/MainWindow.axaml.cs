using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Local;
using MyPersonalDrive.ViewModels.Sync;
using static MyPersonalDrive.Views.RowGestures;

namespace MyPersonalDrive.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The string table. Code-behind builds a dozen dialogs by hand, so it needs the same access
    /// the view models get through <c>ObservableObject.Loc</c>.
    /// </summary>
    private static Localizer Loc => Localizer.Instance;

    private readonly MainWindowDialogs _dialogs;

    public MainWindow()
    {
        InitializeComponent();
        _dialogs = new MainWindowDialogs(this);
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;

        // Read before anything narrows them, so compact mode has something to put back and the
        // two numbers live in exactly one place — the XAML attributes.
        _fullMinWidth = MinWidth;
        _fullMinHeight = MinHeight;
        Closing += OnClosingRememberCompactPosition;

        // Everything else on the keyboard (docs/PLAN-UX-ROUND-3.md X5). Bubble at the window, not
        // KeyBindings and not a per-control handler: a focused TextBox marks Ctrl+A, Delete and F2
        // as handled while it is doing its own editing, so a bubble-phase handler is guarded
        // against stealing them for free — which is exactly the concern that kept Ctrl+A scoped to
        // one ListBox before, and the reason the tile modes never got it (an ItemsRepeater takes no
        // focus, so it has no KeyDown of its own to hang anything on).
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>
    /// Window-level, not the explorer's: with nothing focused a key press routes to the window, so
    /// a handler on the explorer would never see F5 or Escape then. What the keys do lives in
    /// <see cref="ExplorerShortcuts"/>.
    /// </summary>
    private void OnWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            ExplorerShortcuts.Handle(e, viewModel, ExplorerViewHost);
        }
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsCompactMode) && sender is MainWindowViewModel compactViewModel)
        {
            ApplyCompactMode(compactViewModel, compactViewModel.IsCompactMode);
        }
    }

    private readonly double _fullMinWidth;
    private readonly double _fullMinHeight;

    /// <summary>
    /// What the window chrome is currently set up for, or <c>null</c> before the first apply. The
    /// null state is what keeps the initial apply from counting as a transition: entering compact
    /// records the size to restore, and leaving it records where the strip sat, but doing either
    /// on launch would overwrite last session's values with this session's defaults.
    /// </summary>
    private bool? _compactChromeApplied;

    /// <summary>The strip's own width and its floor. Height is left to the content.</summary>
    private const double CompactWindowWidth = 420;
    private const double CompactMinWidth = 280;
    private const double CompactMinHeight = 32;

    /// <summary>
    /// Resizes, pins and places the window for the compact strip, or puts it back
    /// (docs/PLAN-COMPACT-VIEW.md). Not business logic living in code-behind: `MinWidth`,
    /// `SizeToContent`, `Topmost` and `Position` are window properties with no view-model
    /// equivalent, and the view model deliberately owns none of them.
    ///
    /// Order matters on the way in — the XAML minimums are 960x600, so `Width` has to be narrowed
    /// *after* `MinWidth` drops or the assignment is silently clamped straight back up.
    /// </summary>
    private void ApplyCompactMode(MainWindowViewModel viewModel, bool compact)
    {
        if (_compactChromeApplied == compact)
        {
            return;
        }

        var isTransition = _compactChromeApplied is not null;

        if (compact)
        {
            if (isTransition)
            {
                viewModel.SaveRestoreWindowSize(Width, Height);
            }

            MinWidth = CompactMinWidth;
            MinHeight = CompactMinHeight;
            // Height follows the strip rather than a constant: the row's own padding, font size
            // and DPI decide how tall one line of it is, and a hard-coded number would clip it on
            // any machine that disagrees.
            SizeToContent = SizeToContent.Height;
            Width = CompactWindowWidth;
            Topmost = true;

            if (viewModel.CompactWindowPosition is { } position)
            {
                Position = new PixelPoint(position.Left, position.Top);
            }
        }
        else
        {
            if (isTransition)
            {
                viewModel.SaveCompactWindowPosition(Position.X, Position.Y);
            }

            Topmost = false;
            SizeToContent = SizeToContent.Manual;
            MinWidth = _fullMinWidth;
            MinHeight = _fullMinHeight;

            if (viewModel.RestoreWindowSize is { } size)
            {
                Width = size.Width;
                Height = size.Height;
            }
        }

        _compactChromeApplied = compact;
    }

    /// <summary>
    /// The strip's position is saved when it is expanded, which never happens if the app is closed
    /// while still compact — so it is saved on the way out too.
    /// </summary>
    private void OnClosingRememberCompactPosition(object? sender, WindowClosingEventArgs e)
    {
        if (_compactChromeApplied == true && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveCompactWindowPosition(Position.X, Position.Y);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _dialogs.Attach(viewModel);

        viewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        viewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        ApplyCompactMode(viewModel, viewModel.IsCompactMode);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch
            {
                // The view-model already surfaced the error in the status panel.
            }

            // Something has to be focused for arrow keys and Enter to do anything, and nothing was:
            // the app opened with focus nowhere, so the keyboard did not work until the user had
            // clicked a row first (docs/PLAN-UX-ROUND-3.md X5). Only in list mode — an
            // ItemsRepeater takes no focus, and the window-level shortcuts default to the cloud
            // pane anyway, so the tile modes lose nothing by starting unfocused.
            if (viewModel.IsListView)
            {
                ExplorerViewHost.FocusList();
            }
        }
    }

}
