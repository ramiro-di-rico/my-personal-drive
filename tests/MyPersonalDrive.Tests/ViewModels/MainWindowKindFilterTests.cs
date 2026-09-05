using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md M6, type filtering. The rules that matter: the filter is a view of
/// what was already loaded (so clearing it needs no CLI call), it never touches the metrics panel
/// (which answers "what is in this folder", not "what am I looking at"), and it does not survive
/// into a listing where the chosen kind doesn't exist — a folder that opens empty because of a
/// filter chosen elsewhere reads as a broken app.
///
/// The listing is populated through <c>DisplayItems</c> rather than by running a CLI command: the
/// production path marshals through <c>Dispatcher.UIThread.InvokeAsync</c>, which never completes
/// without a running Avalonia dispatcher, so a test driving it would hang instead of failing.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowKindFilterTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.KindFilter").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-filter-{Guid.NewGuid():N}.db");

    public MainWindowKindFilterTests()
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

    private static DriveItem Item(string name, long size)
        => new($"/my-files/{name}", name, IsFolder: false, Size: size,
            ModifiedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private MainWindowViewModel Build()
    {
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var syncStore = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, syncStore, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(syncStore, syncExecutor, new SyncCrashRecovery(syncStore));

        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel);
    }

    /// <summary>A mixed folder: two images, one video, one PDF.</summary>
    private MainWindowViewModel LoadMixedFolder()
    {
        var viewModel = Build();
        viewModel.DisplayItems([Item("a.jpg", 100), Item("b.jpg", 200), Item("c.webm", 5000), Item("d.pdf", 300)]);
        return viewModel;
    }

    private static KindFilterViewModel Chip(MainWindowViewModel viewModel, FileKind? kind)
        => Assert.Single(viewModel.KindFilters, chip => chip.Kind == kind);

    [Fact]
    public void TheChips_OfferOnlyTheKindsThatArePresent()
    {
        var viewModel = LoadMixedFolder();

        Assert.Contains(viewModel.KindFilters, chip => chip.Kind is null);
        Assert.Contains(viewModel.KindFilters, chip => chip.Kind == FileKind.Image);
        Assert.Contains(viewModel.KindFilters, chip => chip.Kind == FileKind.Video);
        Assert.DoesNotContain(viewModel.KindFilters, chip => chip.Kind == FileKind.Audio);
        Assert.Equal(2, Chip(viewModel, FileKind.Image).Count);
    }

    [Fact]
    public void AFolderWithASingleKind_OffersNoChips()
    {
        var viewModel = Build();

        viewModel.DisplayItems([Item("a.jpg", 100), Item("b.jpg", 200)]);

        // Every chip would be a no-op, and "Todos" on its own is noise.
        Assert.Empty(viewModel.KindFilters);
    }

    [Fact]
    public async Task ApplyingAFilter_ShowsOnlyThatKind()
    {
        var viewModel = LoadMixedFolder();

        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.All(viewModel.RootItems, node => Assert.Equal(FileKind.Image, node.FileKind));
        Assert.Contains("2", viewModel.FilterSummary);
        Assert.Contains("4", viewModel.FilterSummary);
    }

    [Fact]
    public async Task AFilter_DoesNotChangeTheMetricsPanel()
    {
        var viewModel = LoadMixedFolder();
        var totalBefore = viewModel.Metrics.TotalSizeText;

        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync();

        Assert.Equal(totalBefore, viewModel.Metrics.TotalSizeText);
        Assert.Equal("4 files · 0 folders", viewModel.Metrics.Headline);
    }

    [Fact]
    public async Task ClickingTheActiveChipAgain_ClearsTheFilter()
    {
        var viewModel = LoadMixedFolder();
        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync();

        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync();

        Assert.Equal(4, viewModel.RootItems.Count);
        Assert.Equal(string.Empty, viewModel.FilterSummary);
        Assert.True(Chip(viewModel, null).IsActive);
    }

    [Fact]
    public async Task TheTodosChip_ClearsTheFilter()
    {
        var viewModel = LoadMixedFolder();
        await Chip(viewModel, FileKind.Video).ApplyCommand.ExecuteAsync();

        await Chip(viewModel, null).ApplyCommand.ExecuteAsync();

        Assert.Equal(4, viewModel.RootItems.Count);
    }

    [Fact]
    public async Task SortingWhileFiltered_KeepsTheFilter()
    {
        var viewModel = LoadMixedFolder();
        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync();

        await viewModel.SortBySizeCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.Equal("b.jpg", viewModel.RootItems[0].DisplayName);
    }

    [Fact]
    public async Task AFilterThatMatchesNothingInTheNextListing_IsDropped()
    {
        var viewModel = Build();
        viewModel.DisplayItems([Item("a.jpg", 100), Item("c.webm", 5000)]);
        await Chip(viewModel, FileKind.Video).ApplyCommand.ExecuteAsync();
        Assert.Single(viewModel.RootItems);

        viewModel.DisplayItems([Item("b.pdf", 100), Item("d.txt", 200)]);

        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.Equal(string.Empty, viewModel.FilterSummary);
    }

    // ---------------------------------------------------------------- search (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.1)

    [Fact]
    public void SearchText_NarrowsToMatchingNames_CaseInsensitively()
    {
        var viewModel = LoadMixedFolder();

        viewModel.SearchText = "JPG";

        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.All(viewModel.RootItems, node => Assert.Contains("jpg", node.DisplayName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("2", viewModel.FilterSummary);
        Assert.Contains("4", viewModel.FilterSummary);
    }

    [Fact]
    public async Task SearchText_CombinesWithTheActiveKindChip()
    {
        var viewModel = LoadMixedFolder();
        await Chip(viewModel, FileKind.Image).ApplyCommand.ExecuteAsync(); // narrows to a.jpg, b.jpg

        viewModel.SearchText = "a"; // narrows further to a.jpg only

        Assert.Equal("a.jpg", Assert.Single(viewModel.RootItems).DisplayName);
    }

    [Fact]
    public void ClearingSearchText_RestoresEveryItem()
    {
        var viewModel = LoadMixedFolder();
        viewModel.SearchText = "jpg";

        viewModel.SearchText = "";

        Assert.Equal(4, viewModel.RootItems.Count);
        Assert.Equal(string.Empty, viewModel.FilterSummary);
    }

    [Fact]
    public void NavigatingToTheNextFolder_DropsTheSearchText()
    {
        var viewModel = LoadMixedFolder();
        viewModel.SearchText = "jpg";

        viewModel.DisplayItems([Item("report.pdf", 100), Item("notes.txt", 200)]);

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.Equal(string.Empty, viewModel.FilterSummary);
    }

    [Fact]
    public void SearchText_ThatMatchesNothing_ShowsAnEmptyListing_NotAnError()
    {
        var viewModel = LoadMixedFolder();

        viewModel.SearchText = "does-not-exist-anywhere";

        Assert.Empty(viewModel.RootItems);
        Assert.Contains("0", viewModel.FilterSummary);
    }
}
