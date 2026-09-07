using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The point of docs/PLAN-I18N.md §6.3: a status line written before the language changed has to
/// follow it, and the picker has to persist.
///
/// These tests drive <see cref="Localizer.Instance"/>, which is process-wide — every one of them
/// restores English in a finally, and they share the app-data collection so they never run
/// alongside another test reading a rendered sentence.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowLanguageTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Language").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-language-{Guid.NewGuid():N}.db");

    public MainWindowLanguageTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Localizer.Instance.SetLanguage(LanguageCatalog.DefaultCode);
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

    private MainWindowViewModel Build(AppSettingsService settings)
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            settings,
            panel);
    }

    /// <summary>
    /// The chips, the rows and the sync pairs are their own binding sources, so the window's
    /// OnAllPropertiesChanged never reached them. Switching to Spanish left "All (14) Folders (8)"
    /// sitting over a fully translated toolbar until something re-listed the folder — visible in a
    /// screenshot, invisible to every test, because the tests all asserted on the window view
    /// model, which was the one object that did update (docs/PLAN-UX-ROUND-3.md X8).
    /// </summary>
    [Fact]
    public void TheTypeFilterChips_FollowTheLanguage()
    {
        var vm = Build(new AppSettingsService());
        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("en");
        vm.DisplayItems([
            new DriveItem("/my-files/a.jpg", "a.jpg", IsFolder: false, Size: 10),
            new DriveItem("/my-files/notes.txt", "notes.txt", IsFolder: false, Size: 10),
        ]);
        var all = vm.KindFilters.Single(chip => chip.Kind is null);
        Assert.Equal("All", all.Label);

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        // Not re-listed, not rebuilt — the same chip object has to answer differently now.
        Assert.Equal("Todos", all.Label);
    }

    [Fact]
    public void ALanguageChange_TellsTheChipsAndRowsToReRead()
    {
        var vm = Build(new AppSettingsService());
        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("en");
        vm.DisplayItems([
            new DriveItem("/my-files/a.jpg", "a.jpg", IsFolder: false, Size: 10),
            new DriveItem("/my-files/notes.txt", "notes.txt", IsFolder: false, Size: 10),
        ]);

        var chipNotified = false;
        var rowNotified = false;
        vm.KindFilters.Single(chip => chip.Kind is null).PropertyChanged += (_, _) => chipNotified = true;
        vm.RootItems[0].PropertyChanged += (_, _) => rowNotified = true;

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        // A label that reads correctly is worthless if nothing tells the binding to read it again.
        Assert.True(chipNotified);
        Assert.True(rowNotified);
    }

    [Fact]
    public void AStandingStatusLine_FollowsTheLanguage()
    {
        var vm = Build(new AppSettingsService());
        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("en");

        // A message the view model wrote itself, not one handed to it already rendered.
        Assert.Equal(StringKeys.Status.PickCliInitial, vm.StatusText.Key);
        Assert.Equal("Choose a CLI executable to get started.", vm.StatusMessage);

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        Assert.Equal("Seleccioná un ejecutable de la CLI para empezar.", vm.StatusMessage);
    }

    /// <summary>
    /// The other half of the contract: text the view already rendered (the drag handlers do this)
    /// is shown as given and must not be looked up as if it were a key.
    /// </summary>
    [Fact]
    public void AVerbatimStatusLine_IsLeftAlone()
    {
        var vm = Build(new AppSettingsService());
        vm.StatusMessage = "raw text from the view";

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        Assert.Null(vm.StatusText.Key);
        Assert.Equal("raw text from the view", vm.StatusMessage);
    }

    [Fact]
    public void DerivedLabelsFollowTheLanguage()
    {
        var vm = Build(new AppSettingsService());
        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("en");
        // Unauthenticated, so the offered remedy is reconnect rather than retry.
        Assert.Equal("Reconnect", vm.StatusActionLabel);

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        Assert.Equal("Reconectar", vm.StatusActionLabel);
        Assert.Contains("Explorador", vm.BrowserHeaderTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheLanguage_PersistsIt()
    {
        var settings = new AppSettingsService();
        var vm = Build(settings);

        vm.SelectedLanguage = LanguageCatalog.ResolveOrDefault("es");

        Assert.Equal("es", settings.Load().LanguageOrDefault());
    }

    [Fact]
    public void ThePickerOffersEveryLanguageInTheCatalog()
        => Assert.Equal(LanguageCatalog.Available, Build(new AppSettingsService()).Languages);
}
