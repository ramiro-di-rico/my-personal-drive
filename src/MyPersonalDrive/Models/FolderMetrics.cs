namespace MyPersonalDrive.Models;

/// <summary>
/// One row of the type histogram: how many items of this kind, and how much they weigh.
/// </summary>
public sealed record FolderKindBucket(FileKind Kind, int Count, long TotalSize);

/// <summary>
/// What a directory contains. Two flavors, and the difference matters more than anything else here
/// (docs/PLAN-BROWSER-VIEWS.md §5):
///
/// <list type="bullet">
/// <item><b>Shallow</b> (<see cref="IsDeep"/> false) — direct children only, computed for free from
/// the listing already on screen. <see cref="TotalSize"/> then covers <em>this folder's files</em>
/// and nothing inside its subfolders.</item>
/// <item><b>Deep</b> — a recursive walk, which on this CLI costs ~3.5 s per folder
/// (PLAN-LOCAL-SYNC Appendix A #11a) and is therefore only ever user-initiated.</item>
/// </list>
///
/// <see cref="UnknownSizeCount"/> exists so the UI can never present a total as complete when it
/// isn't: sizes come from a file's <c>activeRevision</c>, and a node without one contributes
/// nothing to the sum. Same reason <see cref="FolderCount"/> is reported next to a shallow total.
///
/// Sizes are the CLI's <em>claimed</em> sizes — the original local file's size at upload time, not
/// the encrypted size Proton stores — so these totals are expected not to match Proton's own quota
/// display. See <see cref="DriveItem"/>.
/// </summary>
public sealed record FolderMetrics(
    string Path,
    bool IsDeep,
    bool IsComplete,
    int FileCount,
    int FolderCount,
    long TotalSize,
    int UnknownSizeCount,
    IReadOnlyList<FolderKindBucket> Buckets,
    IReadOnlyList<DriveItem> LargestItems,
    DateTimeOffset? NewestModifiedAt,
    DateTimeOffset? OldestModifiedAt,
    int ScannedFolderCount,
    DateTimeOffset ComputedAt)
{
    public bool IsEmpty => FileCount == 0 && FolderCount == 0;

    public static FolderMetrics Empty(string path, DateTimeOffset computedAt) => new(
        Path: path,
        IsDeep: false,
        IsComplete: true,
        FileCount: 0,
        FolderCount: 0,
        TotalSize: 0,
        UnknownSizeCount: 0,
        Buckets: [],
        LargestItems: [],
        NewestModifiedAt: null,
        OldestModifiedAt: null,
        ScannedFolderCount: 1,
        ComputedAt: computedAt);
}
