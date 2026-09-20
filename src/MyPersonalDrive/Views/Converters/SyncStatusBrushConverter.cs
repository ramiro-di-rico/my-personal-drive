using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// The colour of a row's sync badge, as one of the theme's existing status brushes — never a
/// literal colour, so both themes stay correct (the <c>a11y-theming</c> skill). Colour is the
/// secondary channel here; <see cref="SyncStatusIconConverter"/> carries the meaning.
/// </summary>
public sealed class SyncStatusBrushConverter : IValueConverter
{
    public static SyncStatusBrushConverter Instance { get; } = new();

    private static string ResourceKeyFor(SyncNodeStatus status) => status switch
    {
        SyncNodeStatus.Synced => "StatusOkBrush",
        SyncNodeStatus.Syncing or SyncNodeStatus.Pending => "StatusSyncingBrush",
        SyncNodeStatus.PartiallySynced or SyncNodeStatus.OutOfDate => "StatusWarningBrush",
        SyncNodeStatus.Conflict => "StatusDegradedBrush",
        SyncNodeStatus.Failed => "StatusErrorBrush",
        _ => "StatusOfflineBrush",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = ResourceKeyFor(value is SyncNodeStatus status ? status : SyncNodeStatus.Unknown);
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Resources.TryGetResource(key, application.ActualThemeVariant, out var resource)
            ? resource as IBrush
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Badges are display-only.");
}
