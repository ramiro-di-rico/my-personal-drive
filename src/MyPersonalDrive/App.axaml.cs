using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
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
            // The one active provider (docs/PLAN-CLOUD-PROVIDERS.md P1); everything below talks to
            // it through ICloudDriveProvider, never to ProtonDriveService directly.
            var provider = new ProtonDriveProvider(service);
            var cacheService = new DriveCacheService(Path.Combine(settings.BaseFolder, "cache.db"));

            // Same underlying cache.db as cacheService above; SyncStateStore applies the same
            // shared DriveDatabaseMigrations, so either can be constructed independently.
            var syncStateStore = new SyncStateStore(Path.Combine(settings.BaseFolder, "cache.db"));
            // One suppressor shared by the executor (which registers its own writes and deletions)
            // and the scheduler's watchers (which consult it). Two instances would defeat it —
            // see docs/PLAN-LOCAL-SYNC.md §9.
            var echoSuppressor = new SyncEchoSuppressor();
            var syncExecutor = new SyncExecutor(provider.Operations, syncStateStore, new LocalScanner(), new RemoteScanner(provider), echoSuppressor: echoSuppressor);
            var syncScheduler = new SyncScheduler(
                syncStateStore, syncExecutor, echoSuppressor,
                isAuthenticated: () => settings.Load().IsAuthenticated);
            var syncPanelViewModel = new SyncPanelViewModel(syncStateStore, syncExecutor, new SyncCrashRecovery(syncStateStore), syncScheduler)
            {
                GetRemoteFolderChildren = provider.Operations.ListFolderAsync,
            };

            // Same cache.db again, same shared migrations - see the note above.
            var metricsStore = new FolderMetricsStore(Path.Combine(settings.BaseFolder, "cache.db"));

            var mainWindowViewModel = new MainWindowViewModel(
                provider, cacheService, settings, syncPanelViewModel, releaseFeed: new CliReleaseFeed(),
                metricsStore: metricsStore,
                statsScanner: new FolderStatsScanner(provider));

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel
            };

            // Fire and forget on purpose: the window must not wait on a network round-trip to
            // appear, and a failed check only ever writes text into the settings view.
            _ = mainWindowViewModel.CheckForCliUpdateInBackgroundAsync();

            // Stop the loop before the process exits, so a cycle isn't killed mid-transfer when it
            // could just as easily finish — and so the next start has nothing to recover.
            // Bounded on purpose. This blocks the UI thread, which is acceptable while the window is
            // closing but not indefinitely: a cycle mid-transfer would otherwise hold the app open
            // with a frozen window and no way out. Ten seconds is enough for the loop to observe
            // cancellation and for the executor to kill an in-flight CLI process; past that, exiting
            // anyway is safe because `SyncCrashRecovery` reclaims interrupted queue rows on startup.
            desktop.ShutdownRequested += (_, _) =>
            {
                if (!syncScheduler.StopAsync().Wait(TimeSpan.FromSeconds(10)))
                {
                    CrashLog.Write("Sync scheduler did not stop within 10s of shutdown; exiting anyway.");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
