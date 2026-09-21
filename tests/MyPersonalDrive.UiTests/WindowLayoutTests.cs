using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using MyPersonalDrive.Views;
using Xunit;

namespace MyPersonalDrive.UiTests;

/// <summary>
/// Layout assertions against the real <see cref="MainWindow"/>, laid out at a fixed size in
/// Avalonia's headless platform.
///
/// These exist because of two defects this round shipped, both found by a user looking at a
/// screenshot after 1135 view-model tests had passed:
///
/// - A settings field with <c>HorizontalAlignment="Stretch"</c> and a <c>MaxWidth</c> renders
///   *centred*, not left-aligned, so the field floated in the middle of its card.
/// - The local pane's rows stopped at the end of their own text, because Fluent's Button aligns
///   itself Left and the row root is a Button — while the ListBox and the ListBoxItem around it
///   were both full width, which is why two earlier guesses at the cause changed nothing.
///
/// Neither is visible to a view-model test, and both are one number away from obvious once
/// something measures them. That is all this file does: build the window, lay it out, read
/// <c>Bounds</c>.
///
/// It does not replace looking at the app. Contrast, spacing, whether a colour reads as a warning —
/// none of that is here, and none of it should be faked with a number.
/// </summary>
public class WindowLayoutTests : IDisposable
{
    private const double WindowWidth = 1280;
    private const double WindowHeight = 800;

    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.UiTests").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-ui-{Guid.NewGuid():N}.db");

    public WindowLayoutTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        // Closed, or its fire-and-forget refreshes keep posting to the shared dispatcher and run
        // inside the *next* test's pump — which empties that test's listing and reads as a keyboard
        // that does not work. Every one of these tests passes alone; four of them failed together
        // until this line existed.
        _shown?.Close();
        _shown = null;

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The fake CLI behind the window built by <see cref="Show"/>, for seeding a listing.</summary>
    protected FakeCliExecutor Executor { get; private set; } = new();

    private MainWindowViewModel BuildViewModel()
    {
        new AppSettingsService().Save(new AppSettings
        {
            CliPath = "/usr/bin/proton-drive",
            IsAuthenticated = true,
        });

        Executor = new FakeCliExecutor();
        var provider = new ProtonDriveProvider(new ProtonDriveService(Executor));
        var syncStore = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, syncStore, new LocalScanner(), new RemoteScanner(provider));

        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            new SyncPanelViewModel(syncStore, syncExecutor, new SyncCrashRecovery(syncStore)));
    }

    /// <summary>
    /// A window carrying a real view model, shown and laid out. Deliberately not <c>Show()</c>n
    /// through the normal startup path: <c>OnOpened</c> would run <c>InitializeAsync</c> against
    /// the fake CLI and leave the listing at whatever that returned, and these tests want to place
    /// their own rows.
    /// </summary>
    private MainWindow? _shown;

    protected (MainWindow Window, MainWindowViewModel ViewModel) Show()
    {
        var viewModel = BuildViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = WindowWidth,
            Height = WindowHeight,
        };

        window.Show();
        Layout(window);
        _shown = window;
        return (window, viewModel);
    }

    protected static void Layout(Window window) => Layout(window, WindowWidth);

    /// <summary>
    /// Lays the window out at an explicit width. Setting <c>Window.Width</c> alone is not enough
    /// here: these tests measure and arrange by hand, so the size they pass is the size that
    /// counts, and a narrow-window test that only set the property was still arranged at 1280.
    /// </summary>
    protected static void Layout(Window window, double width)
    {
        window.Measure(new Avalonia.Size(width, WindowHeight));
        window.Arrange(new Avalonia.Rect(0, 0, width, WindowHeight));
        window.UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static DriveItem RemoteItem(string name, bool isFolder = false)
        => new($"/my-files/{name}", name, IsFolder: isFolder, Size: 1024,
            ModifiedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The defect: the local pane's rows stopped at the end of their own text, so Size and Modified
    /// landed at a different x on every line and the hover highlight covered the name only. The
    /// ListBox and the ListBoxItem were already full width — this asserts the third one, the row
    /// itself, which is where two earlier guesses were wrong.
    /// </summary>
    [AvaloniaFact]
    public async Task ALocalPaneRow_FillsTheWidthOfItsListing()
    {
        var (window, viewModel) = Show();

        // A folder this test owns, rather than whatever the real home directory happens to hold —
        // and navigated explicitly, because the window's own InitializeAsync races the layout.
        // Enough rows to fill the pane: with a single short row the listing does not scroll and the
        // row measures nearly full width even when it is wrong.
        var folder = Directory.CreateTempSubdirectory("MyPersonalDrive.UiTests.Local").FullName;
        for (var i = 0; i < 25; i++)
        {
            Directory.CreateDirectory(Path.Combine(folder, $"folder-{i:00}"));
        }
        await viewModel.LocalExplorer.NavigateAsync(folder);
        Layout(window);

        var listing = Named<ListBox>(window, "LocalListing");

        // Against its own ListBoxItem, not against the ListBox: the item is the slot the row is
        // given, and comparing to the outer control folds in the scrollbar and needs a tolerance
        // wide enough to hide the defect. It did, in the first draft of this test — with one short
        // row and 40px of slack, the buggy layout passed.
        var item = listing.GetVisualDescendants().OfType<ListBoxItem>().First();
        var row = listing.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("hoverGroup"));

        Assert.True(
            Math.Abs(row.Bounds.Width - item.Bounds.Width) < 1,
            $"A row is {row.Bounds.Width:0}px inside a {item.Bounds.Width:0}px item. Fluent's Button aligns " +
            "itself Left, so a row whose root is a Button needs HorizontalAlignment=\"Stretch\" or it hugs " +
            "its own text — and then Size and Modified land at a different x on every line.");
    }

    /// <summary>
    /// The other defect: Stretch plus MaxWidth centres. The field has to start at the left of its
    /// column, under the label that names it — not float in the middle with its browse button far
    /// away on the right.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDefaultSyncFolderField_StartsAtTheLeftOfItsRow()
    {
        var (window, viewModel) = Show();
        await viewModel.ShowSettingsCommand.ExecuteAsync();
        Layout(window);

        var field = Named<TextBox>(window, "DefaultSyncFolderBox");
        var row = field.GetVisualParent<Control>()!;
        var offset = field.Bounds.X;

        Assert.True(
            offset < 8,
            $"The field starts {offset:0}px into a {row.Bounds.Width:0}px row, which is what a stretched " +
            "control with a MaxWidth does: it centres in the space it is not allowed to fill.");
    }

    /// <summary>
    /// X1's whole point: a warning is visible in every view, including the two that have no status
    /// panel to put it in. Asserting on the rendered tree rather than on IsStatusBannerVisible,
    /// because the property was always right — it was the placement that was wrong.
    /// </summary>
    [AvaloniaFact]
    public async Task AWarning_IsOnScreenWhileTheUserIsInSettings()
    {
        var (window, viewModel) = Show();
        viewModel.StatusMessage = "Failed to load /my-files: invalid access token";
        typeof(MainWindowViewModel)
            .GetProperty("IsWarning", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(viewModel, true);

        await viewModel.ShowSettingsCommand.ExecuteAsync();
        Layout(window);

        var banner = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "Failed to load /my-files: invalid access token" && block.IsEffectivelyVisible);

        Assert.NotNull(banner);
        Assert.True(banner!.Bounds.Height > 0, "The alert strip is in the tree but has no height.");
    }

    /// <summary>
    /// Not a defect — a decision, pinned so it is not "fixed" later. The gap to the right of a
    /// remote row is the space its hover actions occupy; the row itself is full width. This was
    /// misread as a layout bug once already.
    /// </summary>
    [AvaloniaFact]
    public void ARemoteRow_IsFullWidth_WithItsHoverActionsReservedInside()
    {
        var (window, viewModel) = Show();
        viewModel.DisplayItems([RemoteItem("Development", isFolder: true), RemoteItem("notes.txt")]);
        Layout(window);

        var listing = Named<ListBox>(window, "ListModeListing");
        var row = listing.GetVisualDescendants().OfType<Grid>()
            .First(grid => grid.Classes.Contains("hoverGroup"));

        Assert.True(
            row.Bounds.Width > listing.Bounds.Width - 40,
            $"The row grid is {row.Bounds.Width:0}px inside a {listing.Bounds.Width:0}px listing.");

        // The name button stops short of the row, and that is the reserved strip.
        var nameButton = row.GetVisualDescendants().OfType<Button>().First();
        Assert.True(nameButton.Bounds.Width < row.Bounds.Width);
    }

    /// <summary>
    /// docs/PLAN-SYNC-VISIBILITY.md S6. The badge sits inside the name button, beside a name that
    /// is already trimmed to fit (docs/PLAN-UX-ROUND-4.md Y5) — so the thing to check is that at a
    /// narrow width the badge is still fully on screen and the name still has room, rather than the
    /// badge being pushed out of the pane or squeezing the name to nothing. Only this project can
    /// see that: a view-model test knows the badge exists, not where it landed.
    /// </summary>
    [AvaloniaFact]
    public void ASyncBadge_FitsBesideTheName_EvenInANarrowWindow()
    {
        var (window, viewModel) = Show();
        viewModel.DisplayItems([RemoteItem("a-very-long-file-name-that-will-certainly-be-trimmed.txt")]);
        foreach (var node in viewModel.RootItems)
        {
            node.SyncStatus = SyncNodeStatus.Syncing;
        }

        Layout(window, 640);

        var listing = Named<ListBox>(window, "ListModeListing");
        var row = listing.GetVisualDescendants().OfType<Grid>().First(grid => grid.Classes.Contains("hoverGroup"));
        var badge = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Single(path => path.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) is { Length: > 0 });
        var name = row.GetVisualDescendants().OfType<TextBlock>().First();

        Assert.True(badge.Bounds.Width > 0 && badge.Bounds.Height > 0, "The badge has no size, so it is not being drawn.");
        Assert.True(name.Bounds.Width > 40, $"The name is down to {name.Bounds.Width:0}px, so the badge is eating the row.");

        var badgeRight = ((Visual)badge).TranslatePoint(new Avalonia.Point(badge.Bounds.Width, 0), listing)!.Value.X;
        Assert.True(
            badgeRight <= listing.Bounds.Width,
            $"The badge ends {badgeRight:0}px into a {listing.Bounds.Width:0}px listing, so it is off the pane.");
    }

    /// <summary>
    /// docs/PLAN-COMPACT-VIEW.md. The strip replaces the window rather than joining it, so the
    /// thing worth measuring is that the rest is genuinely gone — the header, the view tabs and the
    /// active view. A view-model test can only see the bool.
    /// </summary>
    [AvaloniaFact]
    public async Task CompactMode_LeavesTheStripAndNothingElse()
    {
        var (window, viewModel) = Show();
        var tab = window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == Localizer.Instance.T(StringKeys.Nav.Explorer));
        Assert.True(tab.IsEffectivelyVisible);

        await viewModel.ToggleCompactModeCommand.ExecuteAsync();
        Layout(window, CompactWidth);

        Assert.False(tab.IsEffectivelyVisible, "The view tabs are still on screen, so the window did not collapse.");
        Assert.NotNull(ExpandButton(window));
    }

    /// <summary>
    /// The strip has to survive its own minimum width: this is the one layout in the app whose
    /// width is set by the feature rather than by the user, and the way out of compact mode is the
    /// only control on it. A clipped expand button would strand the window as a strip.
    /// </summary>
    [AvaloniaFact]
    public async Task TheWayOutOfCompactMode_IsOnScreenAtTheNarrowestWidth()
    {
        var (window, viewModel) = Show();
        await viewModel.ToggleCompactModeCommand.ExecuteAsync();
        Layout(window);

        var expand = ExpandButton(window)!;
        Assert.True(expand.Bounds.Width > 0 && expand.Bounds.Height > 0, "The expand button has no size, so it is not being drawn.");

        // The strip is squeezed directly rather than through the window. Compact mode turns on
        // SizeToContent, and UpdateLayout then re-arranges the window from its own Width — so
        // Layout(window, 280) is undone before it can be read, and the strip measures 1280 wide,
        // which is the one width that needs no checking.
        var strip = expand.GetVisualAncestors().OfType<Border>().First();
        strip.Measure(new Avalonia.Size(CompactWidth, double.PositiveInfinity));
        strip.Arrange(new Avalonia.Rect(0, 0, CompactWidth, strip.DesiredSize.Height));

        var right = ((Visual)expand).TranslatePoint(new Avalonia.Point(expand.Bounds.Width, 0), strip)!.Value.X;
        Assert.True(
            right <= CompactWidth,
            $"The expand button ends {right:0}px into a {CompactWidth:0}px strip, so the only way back to the full window is off screen.");

        // The strip is one row: taller than that means something wrapped instead of trimming.
        Assert.True(strip.Bounds.Height < 48, $"The strip is {strip.Bounds.Height:0}px tall, so its contents wrapped.");
    }

    /// <summary>The strip's floor, mirroring <c>MainWindow.CompactMinWidth</c>.</summary>
    private const double CompactWidth = 280;

    /// <summary>Found by its accessible name, which is the same thing a screen reader would use.</summary>
    private static Button? ExpandButton(Window window)
        => window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            button.IsEffectivelyVisible
            && (string?)button.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)
                == Localizer.Instance.T(StringKeys.Compact.ExitTooltip));

    /// <summary>
    /// The badge's meaning reaches a screen reader (the a11y-theming skill): a glyph with no
    /// accessible name says nothing, and here it is the only carrier of the information.
    /// </summary>
    [AvaloniaFact]
    public void ASyncBadge_AnnouncesItsState()
    {
        var (window, viewModel) = Show();
        viewModel.DisplayItems([RemoteItem("notes.txt")]);
        foreach (var node in viewModel.RootItems)
        {
            node.SyncStatus = SyncNodeStatus.Failed;
        }

        Layout(window);

        var listing = Named<ListBox>(window, "ListModeListing");
        var named = listing.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Select(path => path.GetValue(Avalonia.Automation.AutomationProperties.NameProperty))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

        Assert.Contains(SyncStatusPresenter.Describe(SyncNodeStatus.Failed), named);
    }
}
