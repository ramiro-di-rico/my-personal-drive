using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using SkiaSharp;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services;

/// <summary>
/// Renders the pages of a remote PDF for the in-app viewer. Kept behind an interface so view-model
/// tests can hand back canned pages without a CLI, a filesystem, or PDFium — mirrors
/// <see cref="IImageFilePreviewLoader"/>/<see cref="ITextFilePreviewLoader"/>.
/// </summary>
public interface IPdfFilePreviewLoader
{
    Task<PdfFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real loader. Same download-read-delete shape as <see cref="ImageFilePreviewService"/>, plus
/// rendering: PDFium (via the PDFtoImage package) rasterizes each page into an
/// <see cref="SKBitmap"/>, which gets PNG-encoded here so the View can decode it with the exact same
/// <c>Views.Converters.BytesToBitmapConverter</c> the image viewer already uses — nothing PDF- or
/// SkiaSharp-specific needs to reach the ViewModel layer (AGENTS.md: view models never touch
/// Avalonia types, and this keeps them from needing to know about SkiaSharp either).
/// </summary>
public sealed class PdfFilePreviewService : IPdfFilePreviewLoader
{
    private readonly IDriveOperations _operations;
    private readonly string _tempRoot;

    public PdfFilePreviewService(IDriveOperations operations, string? tempRoot = null)
    {
        _operations = operations;
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "MyPersonalDrive", "preview");
    }

    public async Task<PdfFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder)
        {
            throw new LocalizedInvalidOperationException(
                "Folders have no PDF to preview.",
                LocalizedText.Of(StringKeys.Error.PreviewFolderHasNoPdf));
        }

        var directory = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await _operations.DownloadFileAsync(item.Path, directory, cancellationToken);

            // Same reasoning as the text/image loaders: the folder is ours and holds exactly one
            // file, so don't insist the CLI named it exactly what we expected.
            var downloadedPath = Path.Combine(directory, item.Name);
            if (!File.Exists(downloadedPath))
            {
                downloadedPath = Directory.EnumerateFiles(directory).FirstOrDefault()
                    ?? throw new LocalizedIOException(
                        $"The CLI reported success but downloaded nothing for '{item.Name}'.",
                        LocalizedText.Of(StringKeys.Error.CliNothingDownloaded, item.Name));
            }

            var pdfBytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken);
            return Render(item, pdfBytes);
        }
        finally
        {
            // Best effort: a temp folder we couldn't delete must not turn a working preview into an
            // error. The OS reclaims it, and the next preview uses a fresh GUID either way.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Synchronous and CPU-bound (PDFium isn't thread-safe internally, so PDFtoImage serializes
    /// every call behind its own lock anyway) — kept as its own method mainly so a test can exercise
    /// the render step against real PDF bytes without going through a fake <see cref="IDriveOperations"/>.
    /// </summary>
    internal static PdfFilePreview Render(DriveItem item, byte[] pdfBytes)
    {
        var totalPageCount = PDFtoImage.Conversion.GetPageCount(pdfBytes);
        var renderCount = Math.Min(totalPageCount, PdfPreviewPolicy.MaxRenderedPages);

        var pages = new List<byte[]>(renderCount);
        if (renderCount > 0)
        {
            foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdfBytes, new Range(0, renderCount), options: new PDFtoImage.RenderOptions()))
            {
                using (bitmap)
                {
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, quality: 100);
                    pages.Add(encoded.ToArray());
                }
            }
        }

        return new PdfFilePreview(item.Path, item.Name, pages, totalPageCount);
    }
}
