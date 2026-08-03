namespace MyPersonalDrive.Models;

/// <summary>
/// What each side looked like the last time local and remote were confirmed in sync for this
/// path (the "B" in docs/PLAN-LOCAL-SYNC.md §5.1's three-way comparison). Stored as
/// <c>SyncState</c> rows. <see cref="LocalAtSync"/> and <see cref="RemoteAtSync"/> should
/// describe the same content as seen from each side — kept separate (rather than one merged
/// fingerprint) so a fresh local scan and a fresh remote scan can each be compared against
/// their own side's baseline independently, per the decision table's `changed(L,B)` and
/// `changed(R,B)` being separate predicates.
/// </summary>
public sealed record SyncBaselineEntry(
    string RelativePath,
    bool IsFolder,
    NodeFingerprint? LocalAtSync,
    NodeFingerprint? RemoteAtSync);
