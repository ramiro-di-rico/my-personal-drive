namespace MyPersonalDrive.Models;

/// <summary>
/// What the user chose for one parked conflict — docs/PLAN-LOCAL-SYNC.md §5.6's manual resolution.
/// Distinct from <see cref="ConflictPolicy"/>, which is the standing preference configured per pair:
/// this is a one-off decision about a specific file, and it has no <c>Ask</c> member because asking
/// is what produced the parked row in the first place.
/// </summary>
public enum ConflictResolution
{
    /// <summary>Upload the local version over the remote one.</summary>
    KeepLocal,

    /// <summary>Download the remote version over the local one.</summary>
    KeepRemote,

    /// <summary>Rename the local copy aside and keep both, per §5.6's default strategy.</summary>
    KeepBoth
}
