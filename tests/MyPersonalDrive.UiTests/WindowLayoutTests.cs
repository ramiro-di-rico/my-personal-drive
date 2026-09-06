using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
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

    private MainWindowViewModel BuildViewModel()
    {
        new AppSettingsService().Save(new AppSettings
        {
            CliPath = "/usr/bin/proton-drive",
            IsAuthenticated = true,
        });

        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
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
    private (MainWindow Window, MainWindowViewModel ViewModel) Show()
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
        return (window, viewModel);
    }

    private static void Layout(Window window)
    {
        window.Measure(new Avalonia.Size(WindowWidth, WindowHeight));
        window.Arrange(new Avalonia.Rect(0, 0, WindowWidth, WindowHeight));
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
}
