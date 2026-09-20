using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// Turns a <see cref="SyncNodeStatus"/> into the <see cref="StreamGeometry"/> that draws its badge,
/// looked up in <c>Assets/Icons.axaml</c> exactly the way <see cref="FileKindIconConverter"/> does —
/// same reasoning: eleven states across four item templates is forty-four <c>Path</c> elements to
/// keep in step otherwise, and a view model here may not hand out an Avalonia <see cref="Geometry"/>.
///
/// Every state has a distinct <em>shape</em>, not only a distinct colour
/// (docs/PLAN-SYNC-VISIBILITY.md S3): colour alone would leave the badge meaningless to a
/// colour-blind user at the size it is drawn.
///
/// AOT-safe: a dictionary lookup by string key, no reflection.
/// </summary>
public sealed class SyncStatusIconConverter : IValueConverter
{
    public static SyncStatusIconConverter Instance { get; } = new();

    private static string? ResourceKeyFor(SyncNodeStatus status) => status switch
    {
        SyncNodeStatus.Synced => "IconStatusSynced",
        SyncNodeStatus.PartiallySynced => "IconStatusPartial",
        SyncNodeStatus.NotSynced => "IconStatusNotSynced",
        SyncNodeStatus.Pending => "IconStatusPending",
        SyncNodeStatus.Syncing => "IconStatusSyncing",
        SyncNodeStatus.OutOfDate => "IconStatusOutOfDate",
        SyncNodeStatus.Conflict => "IconStatusConflict",
        SyncNodeStatus.Failed => "IconStatusFailed",
        SyncNodeStatus.Excluded => "IconStatusExcluded",
        SyncNodeStatus.Paused => "IconPause",
        SyncNodeStatus.Unknown => "IconStatusUnknown",
        // NotInPair draws nothing at all: a badge on every row outside every pair would be noise
        // on the majority of rows, and the row already says nothing about sync.
        _ => null,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SyncNodeStatus status && ResourceKeyFor(status) is { } key ? Lookup(key) : null;

    private static object? Lookup(string key)
    {
        // Application.Current is null in a design-time or unit-test context; a missing icon must
        // degrade to nothing drawn, never to an exception inside a data template.
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Resources.TryGetResource(key, application.ActualThemeVariant, out var resource)
            ? resource
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Badges are display-only.");
}
