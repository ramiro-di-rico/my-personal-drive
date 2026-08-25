using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Views.Converters;

/// <summary>
/// Turns a <see cref="FileKind"/> into the <see cref="StreamGeometry"/> that draws it, looked up by
/// key in the application's resources (<c>Assets/Icons.axaml</c>).
///
/// This lives in <c>Views/</c> on purpose. The alternative that keeps everything in XAML is one
/// <c>Path</c> per kind with <c>IsVisible</c> bound to a per-kind boolean — twelve kinds across
/// three item templates is thirty-six elements to keep in sync, which is worse than one converter.
/// The other alternative, exposing a <see cref="Geometry"/> from the view model, is out: view models
/// here never touch Avalonia types (AGENTS.md). A converter is a view concern, so it may.
///
/// AOT-safe: a dictionary lookup by string key, no reflection.
/// </summary>
public sealed class FileKindIconConverter : IValueConverter
{
    public static FileKindIconConverter Instance { get; } = new();

    private const string Fallback = "IconFile";

    private static string ResourceKeyFor(FileKind kind) => kind switch
    {
        FileKind.Folder => "IconFolder",
        FileKind.Image => "IconImage",
        FileKind.Video => "IconVideo",
        FileKind.Audio => "IconAudio",
        FileKind.Pdf => "IconPdf",
        FileKind.Archive => "IconArchive",
        FileKind.Code => "IconCode",
        FileKind.Document => "IconDocument",
        FileKind.Spreadsheet => "IconSpreadsheet",
        FileKind.Presentation => "IconPresentation",
        FileKind.Text => "IconDocument",
        _ => Fallback,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is FileKind kind ? ResourceKeyFor(kind) : Fallback;
        return Lookup(key) ?? Lookup(Fallback);
    }

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
        => throw new NotSupportedException("Icons are display-only.");
}
