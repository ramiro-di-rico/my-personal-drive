using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// Decodes a <c>byte[]</c> into an Avalonia <see cref="Bitmap"/> for the image viewer to draw. This
/// lives in <c>Views/</c> for the same reason as <see cref="FileKindIconConverter"/>: view models
/// never touch Avalonia types (AGENTS.md), so the view model exposes the raw downloaded bytes and
/// decoding them is a view concern.
/// </summary>
public sealed class BytesToBitmapConverter : IValueConverter
{
    public static BytesToBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // A file the policy thought was a supported format but SkiaSharp can't actually decode
            // (a corrupt download, or a format variant it doesn't handle) — degrade to nothing drawn
            // rather than crashing the bound control.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Images are display-only.");
}
