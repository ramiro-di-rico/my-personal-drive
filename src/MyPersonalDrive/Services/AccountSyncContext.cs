using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.Services;

/// <summary>
/// Everything the composition root builds for one active provider session — at most one per
/// provider type in this phase (P7 Phase A, docs/PLAN-CLOUD-PROVIDERS.md): Proton's CLI has no
/// multi-account concept of its own, so true multiple accounts of the *same* provider is out of
/// scope here (would need CLI config-directory isolation). A list of these, not two hardcoded
/// slots, so a real multi-account model later extends this rather than replacing it.
/// </summary>
public sealed record AccountSyncContext(
    ICloudDriveProvider Provider,
    string AccountKey,
    string DisplayName,
    DriveCacheService CacheService,
    SyncStateStore StateStore,
    FolderMetricsStore MetricsStore,
    SyncExecutor Executor,
    SyncScheduler Scheduler);
