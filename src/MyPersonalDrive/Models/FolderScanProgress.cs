namespace MyPersonalDrive.Models;

/// <summary>
/// How far a recursive folder scan has got. Deliberately not a percentage: the walk is breadth-first
/// over a tree whose size is unknown until it finishes (docs/PLAN-BROWSER-VIEWS.md M3), so a
/// progress bar would be inventing a denominator. Counts are honest; a fake bar is not.
/// </summary>
public sealed record FolderScanProgress(int FoldersScanned, int FoldersQueued);
