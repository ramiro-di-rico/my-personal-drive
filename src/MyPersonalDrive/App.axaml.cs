using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
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
            // The one active provider (docs/PLAN-CLOUD-PROVIDERS.md P5); everything below talks to
            // it through ICloudDriveProvider, never to a concrete provider type. The catalog is
            // also what the settings view's provider picker enumerates.
            var catalog = new ProviderCatalog();
            // ResolveOrDefault, not ActiveProviderOrDefault alone: the settings value can name a
            // real ProviderId (e.g. OneDrive) that this build still can't construct, and Create
            // throws for exactly that case — resolving first is what keeps a stale/edited
            // settings.json from crashing the app at startup instead of degrading to Proton.
            var provider = catalog.Create(catalog.ResolveOrDefault(settings.Load().ActiveProviderOrDefault()), settings);
            // 'proton:default' — matching exactly what migration 6 backfilled every pre-existing
            // row to (docs/PLAN-CLOUD-PROVIDERS.md P4) — never computed from `provider.Id`
            // here: ProviderId.Proton.ToString() is "Proton" (capital P), which would silently
            // stop matching those rows. A real per-provider/account key mapping is P6's job, once
            // there is a second account whose rows must not collide with these.
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
                statsScanner: new FolderStatsScanner(provider),
                providerCatalog: catalog,
                previewLoader: new TextFilePreviewService(provider.Operations),
                imagePreviewLoader: new ImageFilePreviewService(provider.Operations));

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
