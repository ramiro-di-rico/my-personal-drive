namespace MyPersonalDrive.Models;

/// <summary>
/// Why a sync pair was refused. Typed rather than a sentence, for the reason AGENTS.md already
/// states about errors: callers switch on the value, and the wording lives in one place at the
/// edge (docs/PLAN-I18N.md §9).
///
/// The validators used to return the Spanish sentence itself, which meant the check and the copy
/// were the same thing — untranslatable, and untestable without asserting on prose.
/// </summary>
public enum SyncPairIssueKind
{
    RemotePathNotAbsolute,
    LocalPathMissing,
    LocalPathIsHomeOrRoot,
    LocalPathIsAFile,
    LocalPathNotWritable,

    /// <summary>The exact same local folder is already paired.</summary>
    LocalAlreadySynced,

    /// <summary>A different local folder, but one nests inside the other.</summary>
    LocalOverlaps,

    RemoteAlreadySynced,
    RemoteOverlaps,

    /// <summary>The requested direction would start writing into a folder another pair uploads from.</summary>
    DirectionUnsafeOverlap,

    /// <summary>Not a refusal — a warning shown alongside a preview.</summary>
    NotEnoughFreeSpace,
}

/// <summary>
/// A <see cref="SyncPairIssueKind"/> plus the values its sentence needs, positionally. Rendered by
/// <c>ViewModels.SyncIssuePresenter</c>; <c>Services/</c> never words it.
/// </summary>
public sealed record SyncPairIssue(SyncPairIssueKind Kind, params object?[] Args);
