using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Decides which nodes the in-app PDF viewer offers itself for — the PDF counterpart to
/// <see cref="ImagePreviewPolicy"/>/<see cref="TextPreviewPolicy"/>, checked the same way (the
/// row's eye button and the viewer service, so "previewable" means one thing everywhere).
/// </summary>
public static class PdfPreviewPolicy
{
    /// <summary>
    /// Most bytes downloaded for a preview — a refusal threshold like
    /// <see cref="ImagePreviewPolicy.MaxPreviewBytes"/>, not a truncation limit: rendering an
    /// oversized PDF costs real CPU per page on top of the download, so it's not worth starting.
    /// </summary>
    public const long MaxPreviewBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Most pages actually rendered. A PDF can run to hundreds of pages, and each one is a real
    /// PDFium render call — past this the viewer shows only the first pages and says so plainly
    /// (<c>MainWindowViewModel</c>'s PDF viewer note) rather than silently.
    /// </summary>
    public const int MaxRenderedPages = 20;

    /// <summary>
    /// Whether the viewer should offer itself for <paramref name="item"/>: a <c>.pdf</c> file, small
    /// enough to be worth downloading and rendering. A file whose size the listing didn't report is
    /// allowed through, same as the other preview policies.
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

        return string.Equals(Path.GetExtension(item.Name), ".pdf", StringComparison.OrdinalIgnoreCase);
    }
}
