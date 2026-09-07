using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services;

/// <summary>
/// Turns one image into a one-page PDF, so <see cref="PdfMergeService"/> can join images and PDFs
/// with the same <c>PdfMerger</c> call it already uses for PDFs alone. Converting on the way in,
/// rather than building the merged document page by page, is what keeps the PDF-to-PDF path — the
/// tested one — untouched by the existence of images.
/// </summary>
public static class ImageToPdfPageConverter
{
    /// <summary>A4 at 72 dpi, the size <see cref="PdfDocumentBuilder"/> pages are measured in.</summary>
    private const double A4Short = 595;
    private const double A4Long = 842;

    /// <summary>
    /// Refuses to embed an image larger than this. The bytes go into the PDF uncompressed-as-given,
    /// so a huge source is a huge page — and in the cloud pane that page gets uploaded.
    /// Deliberately the same ceiling the image viewer already refuses at, so "too big to show" and
    /// "too big to merge" are one number.
    /// </summary>
    public const long MaxImageBytes = ImagePreviewPolicy.MaxPreviewBytes;

    /// <summary>
    /// Writes <paramref name="imagePath"/> into <paramref name="outputPath"/> as a single-page PDF.
    ///
    /// The page takes the image's own orientation: a landscape photo gets a landscape page rather
    /// than being letterboxed into a portrait one. Within that page the image is scaled to fit and
    /// centred, so its aspect ratio is never altered.
    /// </summary>
    public static void Convert(string imagePath, string outputPath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        if (bytes.LongLength > MaxImageBytes)
        {
            throw new LocalizedIOException(
                $"'{Path.GetFileName(imagePath)}' is larger than the {MaxImageBytes} byte merge limit.",
                LocalizedText.Of(StringKeys.Error.PdfMergeImageTooLarge, Path.GetFileName(imagePath)));
        }

        using var codec = SKCodec.Create(new MemoryStream(bytes))
            ?? throw new LocalizedIOException(
                $"SkiaSharp could not decode '{Path.GetFileName(imagePath)}'.",
                LocalizedText.Of(StringKeys.Error.PdfMergeUnreadableImage, Path.GetFileName(imagePath)));

        // JPEG goes in untouched — PdfPig embeds the original bytes, so re-encoding would cost
        // quality for nothing. Every other format is transcoded to PNG, the only other thing
        // PdfPig takes. Neither path resamples: the merged page keeps the source's full detail,
        // which is also why MaxImageBytes exists.
        var isJpeg = codec.EncodedFormat == SKEncodedImageFormat.Jpeg;
        byte[] payload;
        double imageWidth;
        double imageHeight;

        if (isJpeg)
        {
            payload = bytes;
            imageWidth = codec.Info.Width;
            imageHeight = codec.Info.Height;
        }
        else
        {
            using var bitmap = SKBitmap.Decode(bytes)
                ?? throw new LocalizedIOException(
                    $"SkiaSharp could not decode '{Path.GetFileName(imagePath)}'.",
                    LocalizedText.Of(StringKeys.Error.PdfMergeUnreadableImage, Path.GetFileName(imagePath)));

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            payload = encoded.ToArray();
            imageWidth = bitmap.Width;
            imageHeight = bitmap.Height;
        }

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new LocalizedIOException(
                $"'{Path.GetFileName(imagePath)}' reports a zero dimension.",
                LocalizedText.Of(StringKeys.Error.PdfMergeUnreadableImage, Path.GetFileName(imagePath)));
        }

        // The page follows the image, not the other way round. A square image gets a portrait page,
        // which is as good an answer as any and keeps the choice deterministic.
        var landscape = imageWidth > imageHeight;
        var pageWidth = landscape ? A4Long : A4Short;
        var pageHeight = landscape ? A4Short : A4Long;

        var scale = Math.Min(pageWidth / imageWidth, pageHeight / imageHeight);
        var drawnWidth = imageWidth * scale;
        var drawnHeight = imageHeight * scale;
        var left = (pageWidth - drawnWidth) / 2;
        var bottom = (pageHeight - drawnHeight) / 2;
        var placement = new PdfRectangle(left, bottom, left + drawnWidth, bottom + drawnHeight);

        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(pageWidth, pageHeight);

        if (isJpeg)
        {
            page.AddJpeg(payload, placement);
        }
        else
        {
            page.AddPng(payload, placement);
        }

        File.WriteAllBytes(outputPath, builder.Build());
    }
}
