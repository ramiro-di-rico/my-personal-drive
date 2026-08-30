using System.Collections.Frozen;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Decides which nodes the in-app image viewer offers itself for — the image counterpart to
/// <see cref="TextPreviewPolicy"/>, and checked the same way (by the row's eye button and by the
/// viewer service, so "previewable" means one thing).
///
/// Deliberately narrower than <see cref="FileKindClassifier"/>'s own <see cref="FileKind.Image"/>
/// set: that set also covers RAW camera formats (.cr2, .nef, .dng, .raw) and .psd, none of which
/// Avalonia's <c>Bitmap</c> (backed by SkiaSharp) can decode, and .svg, which is vector data a
/// bitmap viewer can't render either. Offering the button for those would just trade a download for
/// an error message.
/// </summary>
public static class ImagePreviewPolicy
{
    /// <summary>
    /// Most bytes downloaded for a preview. Unlike the text viewer this is not a truncation limit —
    /// an image can't be shown partially — it's a refusal threshold: past this, previewing costs
    /// more than it's worth.
    /// </summary>
    public const long MaxPreviewBytes = 25 * 1024 * 1024;

    private static readonly FrozenSet<string> SupportedExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the viewer should offer itself for <paramref name="item"/>: a file, a format
    /// SkiaSharp actually decodes, and small enough to be worth downloading. A file whose size the
    /// listing didn't report is allowed through, same as the text policy.
    /// </summary>
    public static bool CanPreview(DriveItem item)
    {
        if (item.IsFolder)
        {
            return false;
        }

        if (item.Size is { } size && size > MaxPreviewBytes)
        {
            return false;
        }

        var extension = Path.GetExtension(item.Name);
        return extension.Length > 0 && SupportedExtensions.Contains(extension);
    }
}
