using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// Turns the typed reasons <c>Services/</c> produces into sentences. The one place that knows how
/// a refusal or a failure is worded, which is what lets the services stay language-free
/// (docs/PLAN-I18N.md §9).
/// </summary>
public static class SyncIssuePresenter
{
    public static LocalizedText Describe(SyncPairIssue issue) => LocalizedText.Of(KeyFor(issue.Kind), issue.Args);

    private static string KeyFor(SyncPairIssueKind kind) => kind switch
    {
        SyncPairIssueKind.RemotePathNotAbsolute => StringKeys.Issue.RemotePathNotAbsolute,
        SyncPairIssueKind.LocalPathMissing => StringKeys.Issue.LocalPathMissing,
        SyncPairIssueKind.LocalPathIsHomeOrRoot => StringKeys.Issue.LocalPathHomeOrRoot,
        SyncPairIssueKind.LocalPathIsAFile => StringKeys.Issue.LocalPathIsAFile,
        SyncPairIssueKind.LocalPathNotWritable => StringKeys.Issue.LocalPathNotWritable,
        SyncPairIssueKind.LocalAlreadySynced => StringKeys.Issue.LocalAlreadySynced,
        SyncPairIssueKind.LocalOverlaps => StringKeys.Issue.LocalOverlaps,
        SyncPairIssueKind.RemoteAlreadySynced => StringKeys.Issue.RemoteAlreadySynced,
        SyncPairIssueKind.RemoteOverlaps => StringKeys.Issue.RemoteOverlaps,
        SyncPairIssueKind.DirectionUnsafeOverlap => StringKeys.Issue.DirectionUnsafeOverlap,
        _ => StringKeys.Issue.FreeSpace,
    };
}

/// <summary>
/// The sentence the interface leads with for a failed remote operation, chosen from the typed
/// <see cref="DriveErrorKind"/>. The provider's own message is *not* replaced by this — it is shown
/// after it, verbatim, because that sentence is the provider's and is often the only thing that
/// says whether the problem is the user's or ours (docs/PLAN-I18N.md §9).
/// </summary>
public static class DriveErrorPresenter
{
    public static string KeyFor(DriveErrorKind kind) => kind switch
    {
        DriveErrorKind.NotAuthenticated => StringKeys.Error.KindNotAuthenticated,
        DriveErrorKind.NotFound => StringKeys.Error.KindNotFound,
        DriveErrorKind.AlreadyExists => StringKeys.Error.KindAlreadyExists,
        DriveErrorKind.Quota => StringKeys.Error.KindQuota,
        DriveErrorKind.Network => StringKeys.Error.KindNetwork,
        DriveErrorKind.Timeout => StringKeys.Error.KindTimeout,
        DriveErrorKind.Busy => StringKeys.Error.KindBusy,
        DriveErrorKind.PermissionDenied => StringKeys.Error.KindPermissionDenied,
        DriveErrorKind.InvalidArgument => StringKeys.Error.KindInvalidArgument,
        DriveErrorKind.RateLimited => StringKeys.Error.KindRateLimited,
        DriveErrorKind.Conflict => StringKeys.Error.KindConflict,
        _ => StringKeys.Error.KindUnknown,
    };

    /// <summary>The kind's sentence on its own — for a surface with no room for the provider's detail.</summary>
    public static string Describe(DriveErrorKind kind) => Localizer.Instance.T(KeyFor(kind));
}
