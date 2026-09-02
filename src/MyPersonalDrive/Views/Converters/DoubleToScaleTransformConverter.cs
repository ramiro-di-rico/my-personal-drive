using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// Turns <c>MainWindowViewModel.ViewerZoom</c> (a plain <see cref="double"/> — view models never
/// touch Avalonia types, AGENTS.md) into a <see cref="ScaleTransform"/> for a
/// <see cref="Avalonia.Controls.LayoutTransformControl"/>. <c>LayoutTransformControl</c> specifically
/// (not a bare <c>RenderTransform</c>) because it re-measures its child at the scaled size, so the
/// surrounding <c>ScrollViewer</c>'s extent actually shrinks with the zoom — a <c>RenderTransform</c>
/// only changes what's drawn, leaving the same huge scrollable area behind it.
/// </summary>
public sealed class DoubleToScaleTransformConverter : IValueConverter
{
    public static DoubleToScaleTransformConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double zoom && zoom > 0 ? new ScaleTransform(zoom, zoom) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("The zoom slider writes the zoom value directly.");
}
