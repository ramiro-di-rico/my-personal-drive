namespace MyPersonalDrive.Models;

/// <summary>
/// The pure result of reconciliation: a list of actions and flagged conflicts. Touches nothing
/// by itself — see <c>SyncReconciler</c> in Services/Sync.
/// </summary>
public sealed record SyncPlan(
    int PairId,
    IReadOnlyList<SyncAction> Actions,
    IReadOnlyList<SyncConflict> Conflicts,
    SyncPlanStats Stats);
