using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
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
            ApplyTheme(settings.Load().ThemeOrDefault());
            var catalog = new ProviderCatalog();

            // P7 Phase A (docs/PLAN-CLOUD-PROVIDERS.md): a session per provider *type*, not just
            // the one the settings picker names — Proton's CLI has no multi-account concept of its
            // own, so at most one session per provider exists, but both can be active together.
            // Both constructors are cheap and side-effect-free even when unconfigured (an empty
            // CliPath/OneDriveClientId) — real failures surface lazily, on the first actual
            // operation, never here, so building both unconditionally is safe.
            var contexts = catalog.Available
                .Select(descriptor => BuildAccountContext(catalog.Create(descriptor.Id, settings), settings))
                .ToList();

            // Which session the browser opens on: the persisted preference if that provider's
            // session exists (it always will, today — every provider is built above), else
            // whichever one is first, same degrade-gracefully spirit as ProviderCatalog's own
            // ResolveOrDefault.
            var preferredId = settings.Load().ActiveProviderOrDefault();
            var primary = contexts.FirstOrDefault(context => context.Provider.Id == preferredId) ?? contexts[0];
            var others = contexts.Where(context => !ReferenceEquals(context, primary)).ToList();

            var syncPanelViewModel = new SyncPanelViewModel(primary.StateStore, primary.Executor, new SyncCrashRecovery(primary.StateStore), primary.Scheduler, primary.DisplayName)
            {
                GetRemoteFolderChildren = primary.Provider.Operations.ListFolderAsync,
            };
            foreach (var other in others)
            {
                syncPanelViewModel.AddAccount(other.StateStore, other.Executor, new SyncCrashRecovery(other.StateStore), other.Scheduler, other.DisplayName);
            }

            var mainWindowViewModel = new MainWindowViewModel(
                primary.Provider, primary.CacheService, settings, syncPanelViewModel, releaseFeed: new CliReleaseFeed(),
                metricsStore: primary.MetricsStore,
                statsScanner: new FolderStatsScanner(primary.Provider),
                providerCatalog: catalog,
                previewLoader: new TextFilePreviewService(primary.Provider.Operations),
                imagePreviewLoader: new ImageFilePreviewService(primary.Provider.Operations));

            // The console shows every active account's activity regardless of which is browsed —
            // background sync on an account you're not currently looking at is still something
            // you'd want to see happening. The browser itself can now switch to any of them live,
            // no restart (P7 Phase B, docs/PLAN-CLOUD-PROVIDERS.md) — AddBrowsableAccount registers
            // the same per-account toolchain built for the primary above.
            foreach (var other in others)
            {
                mainWindowViewModel.ObserveAdditionalProviderActivity(other.DisplayName, other.Provider);
                mainWindowViewModel.AddBrowsableAccount(
                    other.Provider, other.CacheService,
                    metricsStore: other.MetricsStore,
                    statsScanner: new FolderStatsScanner(other.Provider),
                    previewLoader: new TextFilePreviewService(other.Provider.Operations),
                    imagePreviewLoader: new ImageFilePreviewService(other.Provider.Operations));
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel
            };

            // Fire and forget on purpose: the window must not wait on a network round-trip to
            // appear, and a failed check only ever writes text into the settings view.
            _ = mainWindowViewModel.CheckForCliUpdateInBackgroundAsync();

            // Stop every account's loop before the process exits, so a cycle isn't killed
            // mid-transfer when it could just as easily finish — and so the next start has nothing
            // to recover. Bounded on purpose, per scheduler: this blocks the UI thread, acceptable
            // while the window is closing but not indefinitely — ten seconds each is enough for a
            // loop to observe cancellation and for its executor to kill an in-flight CLI process;
            // past that, exiting anyway is safe because SyncCrashRecovery reclaims interrupted
            // queue rows on the next startup.
            desktop.ShutdownRequested += (_, _) =>
            {
                foreach (var context in contexts)
                {
                    if (!context.Scheduler.StopAsync().Wait(TimeSpan.FromSeconds(10)))
                    {
                        CrashLog.Write($"{context.DisplayName} sync scheduler did not stop within 10s of shutdown; exiting anyway.");
                    }
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme?.ToLowerInvariant() switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default
        };
    }

    private static AccountSyncContext BuildAccountContext(ICloudDriveProvider provider, AppSettingsService settings)
    {
        // Lowercased provider id + ":default" — for Proton this is exactly "proton:default",
        // matching what migration 6 backfilled every pre-existing row to (P4), so existing Proton
        // installs see no change. ":default" rather than a real per-account identity either way —
        // P7's own scope limit (at most one account per provider type) means it doesn't need to be
        // yet; a real per-account key is P7's *general* form, not attempted here.
        var accountKey = $"{provider.Id.ToString().ToLowerInvariant()}:default";
        var dbPath = Path.Combine(settings.BaseFolder, "cache.db");
        var cacheService = new DriveCacheService(dbPath, accountKey);
        // Same underlying cache.db as cacheService above; SyncStateStore/FolderMetricsStore apply
        // the same shared DriveDatabaseMigrations, so each can be constructed independently — and,
        // per P7, once per active provider rather than once for the whole app.
        var syncStateStore = new SyncStateStore(dbPath, accountKey);
        var metricsStore = new FolderMetricsStore(dbPath, accountKey);

        // One suppressor per context, shared only by that context's own executor/scheduler pair
        // (which registers its own writes and deletions) and that pair's own watchers (which
        // consult it). Two instances *within one account* would defeat it (docs/PLAN-LOCAL-SYNC.md
        // §9) — one per account is correct, not a violation of that rule.
        var echoSuppressor = new SyncEchoSuppressor();
        // Which hasher/algorithm-tag pair matches this provider's own Capabilities.RemoteHash —
        // SyncExecutor's own default (Sha1ContentHasher) is only correct for Proton
        // (docs/PLAN-CLOUD-PROVIDERS.md P3/P6, and SyncExecutor's own doc comment on `hasher`).
        var hasher = provider.Capabilities.RemoteHash == RemoteHashAlgorithm.QuickXor
            ? (IContentHasher)new QuickXorHasher()
            : new Sha1ContentHasher();
        // Delta-based scanning only for a provider whose backend actually supports it (P8) — Proton
        // has none, and stays on the full-walk RemoteScanner it always used.
        var deltaScanner = provider.Capabilities.SupportsDelta && provider.DeltaSource is not null
            ? new DeltaRemoteScanner(provider, syncStateStore)
            : null;
        var syncExecutor = new SyncExecutor(
            provider.Operations, syncStateStore, new LocalScanner(), new RemoteScanner(provider),
            echoSuppressor: echoSuppressor, hasher: hasher, remoteHashAlgorithm: provider.Capabilities.RemoteHash,
            deltaScanner: deltaScanner);
        var syncScheduler = new SyncScheduler(
            syncStateStore, syncExecutor, echoSuppressor,
            // The bool that actually matters is this provider's own — mirrors
            // MainWindowViewModel's own IsAuthenticated field selection.
            isAuthenticated: () => provider.Id == ProviderId.OneDrive ? settings.Load().IsOneDriveAuthenticated : settings.Load().IsAuthenticated);

        return new AccountSyncContext(provider, accountKey, provider.DisplayName, cacheService, syncStateStore, metricsStore, syncExecutor, syncScheduler);
    }
}
