using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// Decodes each PNG-encoded PDF page into an Avalonia <see cref="Bitmap"/>, for the PDF viewer's
/// page list. A separate converter from <see cref="BytesToBitmapConverter"/> (rather than an
/// <c>ItemsControl</c> template that calls it per item) so the item template's <c>x:DataType</c> can
/// be the well-known <see cref="Bitmap"/> type and stay on compiled bindings — a <c>DataTemplate</c>
/// over a bare <c>byte[]</c> item has no <c>x:DataType</c> to declare, which would otherwise force
/// the template onto reflection bindings (<c>AvaloniaUseCompiledBindingsByDefault</c> is on
/// project-wide, and reflection bindings pull in <c>RequiresUnreferencedCode</c>/
/// <c>RequiresDynamicCode</c> APIs the AOT publish (see <c>.claude/skills/aot-check</c>) flags).
/// </summary>
public sealed class PdfPagesToBitmapsConverter : IValueConverter
{
    public static PdfPagesToBitmapsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<byte[]> pages)
        {
            return null;
        }

        var bitmaps = new List<Bitmap>(pages.Count);
        foreach (var page in pages)
        {
            if (BytesToBitmapConverter.Instance.Convert(page, typeof(Bitmap), parameter, culture) is Bitmap bitmap)
            {
                bitmaps.Add(bitmap);
            }
        }

        return bitmaps;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("PDF pages are display-only.");
}
