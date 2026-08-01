using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using MyPersonalDrive.Views;

namespace MyPersonalDrive;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsService();
            var locator = new ProtonDriveCliLocator(settings);
            var executor = new ProtonDriveCliExecutor(locator);
            var service = new ProtonDriveService(executor, settings.Load().StrictListingParsing);
            var cacheService = new DriveCacheService(Path.Combine(settings.BaseFolder, "cache.db"));

            // Same underlying cache.db as cacheService above; SyncStateStore applies the same
            // shared DriveDatabaseMigrations, so either can be constructed independently.
            var syncStateStore = new SyncStateStore(Path.Combine(settings.BaseFolder, "cache.db"));
            var syncExecutor = new SyncExecutor(service, syncStateStore, new LocalScanner(), new RemoteScanner(service));
            var syncPanelViewModel = new SyncPanelViewModel(syncStateStore, syncExecutor, new SyncCrashRecovery(syncStateStore));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(service, cacheService, settings, syncPanelViewModel)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
