namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// What the user chose to do with one failed sync action in the failures view
/// (docs/PLAN-UX-ROUND-2.md §6). Modelled on <c>ConflictResolution</c>, which the parked-conflict
/// flow already uses for the same shape of decision.
/// </summary>
public enum SyncFailureDecision
{
    /// <summary>Put it back in the queue with a clean slate, for the next sync to attempt again.</summary>
    Retry,

    /// <summary>
    /// Drop it. The action is deleted rather than recorded as done — it never happened, and
    /// marking it complete would corrupt the baseline the next scan compares against. If the
    /// difference is still real, the next plan proposes it again.
    /// </summary>
    Discard
}
