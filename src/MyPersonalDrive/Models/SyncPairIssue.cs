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

    /// <summary>A different local folder, but one nests inside the other. Sharing the *same* folder is allowed for the shapes <see cref="SharedFolderNeedsOneWay"/>/<see cref="SharedFolderNeedsAdditive"/> describe, so this is only ever nesting.</summary>
    LocalOverlaps,

    RemoteAlreadySynced,
    RemoteOverlaps,

    /// <summary>Sharing a local folder is only safe one-way: a two-way pair would upload the other provider's files and delete against its own remote.</summary>
    SharedFolderNeedsOneWay,

    /// <summary>A download pair sharing a local folder has to be additive — mirroring deletes would delete whatever the other provider put there.</summary>
    SharedFolderNeedsAdditive,

    /// <summary>Not a refusal — a warning shown alongside a preview.</summary>
    NotEnoughFreeSpace,
}

/// <summary>
/// A <see cref="SyncPairIssueKind"/> plus the values its sentence needs, positionally. Rendered by
/// <c>ViewModels.SyncIssuePresenter</c>; <c>Services/</c> never words it.
/// </summary>
public sealed record SyncPairIssue(SyncPairIssueKind Kind, params object?[] Args);
