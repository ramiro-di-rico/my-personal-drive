namespace MyPersonalDrive.Models;

/// <summary>
/// The rendered pages of a remote PDF, as shown by the in-app viewer — one PNG-encoded bitmap per
/// page, produced by <c>Services.PdfFilePreviewService</c> via PDFium (through the PDFtoImage
/// package). Kept as pre-rendered PNG bytes rather than decoded bitmaps, for the same reason as
/// <see cref="ImageFilePreview"/>: view models never touch Avalonia types (AGENTS.md); the View
/// decodes each page with the very same <c>Views.Converters.BytesToBitmapConverter</c> the image
/// viewer already uses — nothing SkiaSharp- or PDF-specific needs to leak past the loader either.
/// </summary>
/// <param name="Path">The remote path the preview came from.</param>
/// <param name="Name">The file's name, for the viewer's header.</param>
/// <param name="Pages">PNG bytes, one entry per rendered page, in order.</param>
/// <param name="TotalPageCount">
/// The PDF's real page count, which can exceed <c>Pages.Count</c> when rendering was capped
/// (<c>PdfPreviewPolicy.MaxRenderedPages</c>) — the viewer needs both numbers to say so honestly.
/// </param>
public sealed record PdfFilePreview(
    string Path,
    string Name,
    IReadOnlyList<byte[]> Pages,
    int TotalPageCount);
