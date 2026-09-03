namespace MyPersonalDrive.Models;

/// <summary>
/// Pre-fills the "Add sync pair" dialog from a right-clicked row (docs/
/// INTERFACE_IMPROVEMENT_PLAN.md Task 6's "Sync Selected Path..." context-menu action) — the side
/// the user right-clicked is known, the other one is still theirs to choose.
/// </summary>
public sealed record SyncPairPrefill(string? RemotePath, string? LocalPath);
