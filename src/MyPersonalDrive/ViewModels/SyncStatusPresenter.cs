using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The wording of a <see cref="SyncNodeStatus"/> — its badge label, which is also the badge's
/// accessible name, and the tooltip that names the pair it is about
/// (docs/PLAN-SYNC-VISIBILITY.md S3). One place, so the cloud pane, the local pane and the
/// Properties dialog cannot drift into three vocabularies for the same eleven states.
/// </summary>
public static class SyncStatusPresenter
{
    private static Localizer Loc => Localizer.Instance;

    public static string KeyFor(SyncNodeStatus status) => status switch
    {
        SyncNodeStatus.Synced => StringKeys.SyncStatus.Synced,
        SyncNodeStatus.PartiallySynced => StringKeys.SyncStatus.PartiallySynced,
        SyncNodeStatus.NotSynced => StringKeys.SyncStatus.NotSynced,
        SyncNodeStatus.Pending => StringKeys.SyncStatus.Pending,
        SyncNodeStatus.Syncing => StringKeys.SyncStatus.Syncing,
        SyncNodeStatus.OutOfDate => StringKeys.SyncStatus.OutOfDate,
        SyncNodeStatus.Conflict => StringKeys.SyncStatus.Conflict,
        SyncNodeStatus.Failed => StringKeys.SyncStatus.Failed,
        SyncNodeStatus.Excluded => StringKeys.SyncStatus.Excluded,
        SyncNodeStatus.Paused => StringKeys.SyncStatus.Paused,
        SyncNodeStatus.NotInPair => StringKeys.SyncStatus.NotInPair,
        _ => StringKeys.SyncStatus.Unknown,
    };

    /// <summary>The badge's label, in the current language.</summary>
    public static string Describe(SyncNodeStatus status) => Loc.T(KeyFor(status));

    /// <summary>
    /// The tooltip: the status, and what it is about — the pair, a provider name, or the
    /// "as of the last sync" qualifier a collapsed folder's answer carries. Falls back to the bare
    /// label when there is no second half worth saying.
    /// </summary>
    public static string Describe(SyncNodeStatus status, string? context)
        => string.IsNullOrWhiteSpace(context)
            ? Describe(status)
            : Loc.F(StringKeys.SyncStatus.TooltipWithPair, Describe(status), context);

    /// <summary>Whether this status is worth drawing a badge for at all.</summary>
    public static bool IsVisible(SyncNodeStatus status) => status is not SyncNodeStatus.NotInPair;
}
