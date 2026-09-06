using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// docs/PLAN-UX-ROUND-3.md X1 and X3 — the two "the app has something to say and no place to say
/// it" items.
///
/// X1 splits one status line across two surfaces: warnings go to a window-level alert strip that no
/// preference can hide and that exists in every view, ordinary progress stays in the status panel's
/// card. The property pair below is the whole contract the markup binds to, so it is what these
/// tests pin — the previous arrangement rendered a failure only inside a panel governed by
/// <c>ShowStatusPanel</c> and only while the explorer was on screen.
///
/// X3 is the empty state: three situations that used to render as the same blank rectangle.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowStatusSurfaceTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.StatusSurface").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-status-{Guid.NewGuid():N}.db");

    public MainWindowStatusSurfaceTests()
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

    private static DriveItem Item(string name)
        => new($"/my-files/{name}", name, IsFolder: false, Size: 100,
            ModifiedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private MainWindowViewModel Build()
    {
        // IsAuthenticated is private-set and read at construction, so the settings file is the way in.
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
    /// The production path to a warning is a failed CLI call, which would need a dispatcher here.
    /// Same reflection seam <see cref="MainWindowHeaderTelemetryTests"/> already uses: the setter
    /// is private because nothing outside the view model may raise a warning, and that is the
    /// property worth keeping — not the test's convenience.
    /// </summary>
    private static void RaiseWarning(MainWindowViewModel viewModel, string message)
    {
        viewModel.StatusMessage = message;
        typeof(MainWindowViewModel)
            .GetProperty("IsWarning", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(viewModel, true);
    }

    [Fact]
    public void AnOrdinaryMessage_StaysInThePanel_AndRaisesNoBanner()
    {
        var viewModel = Build();

        viewModel.StatusMessage = "Loaded 14 items.";

        Assert.True(viewModel.IsInformationalStatus);
        Assert.False(viewModel.IsStatusBannerVisible);
    }

    [Fact]
    public void AWarning_RaisesTheBanner_AndLeavesThePanelCardEmpty()
    {
        var viewModel = Build();

        RaiseWarning(viewModel, "Failed to load /my-files: invalid access token");

        Assert.True(viewModel.IsStatusBannerVisible);
        // The whole point of the split: never the same sentence twice on one screen.
        Assert.False(viewModel.IsInformationalStatus);
        Assert.True(viewModel.HasStatusAction);
    }

    [Fact]
    public async Task DismissingTheBanner_TakesItDown_WithoutTouchingTheMessage()
    {
        var viewModel = Build();
        RaiseWarning(viewModel, "Network unreachable");

        await viewModel.DismissStatusBannerCommand.ExecuteAsync();

        Assert.False(viewModel.IsStatusBannerVisible);
        Assert.Equal("Network unreachable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ANewMessage_BringsTheBannerBack_AfterADismissal()
    {
        var viewModel = Build();
        RaiseWarning(viewModel, "Network unreachable");
        await viewModel.DismissStatusBannerCommand.ExecuteAsync();

        RaiseWarning(viewModel, "Permission denied on /my-files/reports");

        // A dismissal answers one message, not every message that follows it.
        Assert.True(viewModel.IsStatusBannerVisible);
    }

    [Fact]
    public void AnEmptyFolder_SaysSo()
    {
        var viewModel = Build();

        viewModel.DisplayItems([]);

        Assert.True(viewModel.IsListingEmpty);
        Assert.False(viewModel.IsListingFilteredToNothing);
        Assert.Equal("This folder is empty", viewModel.ListingEmptyTitle);
    }

    [Fact]
    public void BeforeTheFirstLoad_ThereIsNoEmptyState()
    {
        var viewModel = Build();

        // "No rows yet" is not "nothing here" — it is "nothing has been asked for yet", and the
        // empty state flashing on the way to the first paint is what this guards.
        Assert.False(viewModel.IsListingEmpty);
    }

    [Fact]
    public void ASearchThatMatchesNothing_ReadsAsAFilter_NotAsAnEmptyFolder()
    {
        var viewModel = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt")]);

        viewModel.SearchText = "zzz";

        Assert.True(viewModel.IsListingEmpty);
        Assert.True(viewModel.IsListingFilteredToNothing);
        Assert.Equal("Nothing matches", viewModel.ListingEmptyTitle);
        // The detail names the count the filter is hiding, so the number on screen is the folder's.
        Assert.Contains("2", viewModel.ListingEmptyDetail);
    }

    [Fact]
    public async Task ClearingTheFilters_BringsTheRowsBack()
    {
        var viewModel = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt")]);
        viewModel.SearchText = "zzz";

        Assert.True(viewModel.ClearFiltersCommand.CanExecute(null));
        await viewModel.ClearFiltersCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.RootItems.Count);
        Assert.False(viewModel.IsListingEmpty);
        Assert.Equal(string.Empty, viewModel.SearchText);
    }

    [Fact]
    public void WithNothingFiltering_TheClearButtonIsNotOffered()
    {
        var viewModel = Build();
        viewModel.DisplayItems([]);

        Assert.False(viewModel.ClearFiltersCommand.CanExecute(null));
    }
}
