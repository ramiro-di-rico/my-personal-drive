using MyPersonalDrive.Services;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The real merge, over real PDF bytes. Everything above this (both explorers) runs against
/// <c>Fakes.FakePdfMergeService</c>, so this is the only place that proves PdfPig actually joins
/// documents the way the feature claims: page count adds up, page order follows the argument
/// order, and the text layer survives — the last one being the whole reason the merge does not go
/// through PDFium, which can only rasterize.
/// </summary>
public class PdfMergeServiceTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.PdfMerge").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a real PDF whose every page carries a distinct, findable line of text.</summary>
    private string MakePdf(string fileName, int pages, string label)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= pages; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText($"{label}{i}", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        }

        var path = Path.Combine(_temp, fileName);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private string Output(string name = "merged.pdf") => Path.Combine(_temp, name);

    /// <summary>Writes a real encoded image of the given pixel size.</summary>
    private string MakeImage(string fileName, int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 4f, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        var path = Path.Combine(_temp, fileName);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task MergeAsync_joins_every_page_of_every_source()
    {
        var service = new PdfMergeService();
        var output = Output();

        var pages = await service.MergeAsync([MakePdf("a.pdf", 2, "A"), MakePdf("b.pdf", 3, "B")], output);

        Assert.Equal(5, pages);
        using var merged = PdfDocument.Open(output);
        Assert.Equal(5, merged.NumberOfPages);
    }

    [Fact]
    public async Task MergeAsync_follows_the_order_it_was_given_not_the_file_names()
    {
        var service = new PdfMergeService();
        var a = MakePdf("a.pdf", 1, "A");
        var b = MakePdf("b.pdf", 1, "B");
        var output = Output();

        // b before a: the dialog's reorder buttons are exactly this, and if the service sorted or
        // ignored the order the feature would silently produce the wrong document.
        await service.MergeAsync([b, a], output);

        using var merged = PdfDocument.Open(output);
        Assert.Contains("B1", merged.GetPage(1).Text);
        Assert.Contains("A1", merged.GetPage(2).Text);
    }

    [Fact]
    public async Task MergeAsync_keeps_the_text_layer_rather_than_rasterizing()
    {
        var service = new PdfMergeService();
        var output = Output();

        await service.MergeAsync([MakePdf("a.pdf", 1, "Hello"), MakePdf("b.pdf", 1, "World")], output);

        using var merged = PdfDocument.Open(output);
        Assert.Contains("Hello1", merged.GetPage(1).Text);
        Assert.Contains("World1", merged.GetPage(2).Text);
    }

    [Fact]
    public async Task MergeAsync_turns_an_image_into_a_page_beside_the_pdfs_pages()
    {
        var service = new PdfMergeService(_temp);
        var output = Output();

        var pages = await service.MergeAsync(
            [MakePdf("a.pdf", 2, "A"), MakeImage("photo.jpg", 800, 600, SKEncodedImageFormat.Jpeg)],
            output);

        Assert.Equal(3, pages);
        using var merged = PdfDocument.Open(output);

        // The PDF's own pages are untouched — adding image support must not have quietly moved the
        // PDF-to-PDF path onto a different mechanism.
        Assert.Contains("A1", merged.GetPage(1).Text);
        Assert.Contains("A2", merged.GetPage(2).Text);

        // And the image landed as an image, at its original pixel size: nothing resamples it.
        var image = Assert.Single(merged.GetPage(3).GetImages());
        Assert.Equal(800, image.WidthInSamples);
        Assert.Equal(600, image.HeightInSamples);
    }

    [Fact]
    public async Task MergeAsync_accepts_formats_beyond_jpeg_by_transcoding_them()
    {
        var service = new PdfMergeService(_temp);
        var output = Output();

        // PdfPig itself only takes JPEG and PNG; a WebP proves the SkiaSharp transcode step runs,
        // so the merge accepts everything the image viewer already displays.
        var pages = await service.MergeAsync(
            [
                MakePdf("a.pdf", 1, "A"),
                MakeImage("shot.png", 400, 300, SKEncodedImageFormat.Png),
                MakeImage("shot.webp", 320, 240, SKEncodedImageFormat.Webp),
            ],
            output);

        Assert.Equal(3, pages);
        using var merged = PdfDocument.Open(output);
        Assert.Single(merged.GetPage(2).GetImages());
        Assert.Single(merged.GetPage(3).GetImages());
    }

    [Fact]
    public async Task MergeAsync_gives_each_image_a_page_in_its_own_orientation()
    {
        var service = new PdfMergeService(_temp);
        var output = Output();

        await service.MergeAsync(
            [
                MakeImage("wide.jpg", 800, 400, SKEncodedImageFormat.Jpeg),
                MakeImage("tall.jpg", 400, 800, SKEncodedImageFormat.Jpeg),
            ],
            output);

        using var merged = PdfDocument.Open(output);

        // A landscape photo gets a landscape page rather than being letterboxed into a portrait
        // one, and vice versa — the whole point of choosing the page per image.
        var wide = merged.GetPage(1);
        var tall = merged.GetPage(2);
        Assert.True(wide.Width > wide.Height, $"A landscape image got a {wide.Width}x{wide.Height} page.");
        Assert.True(tall.Height > tall.Width, $"A portrait image got a {tall.Width}x{tall.Height} page.");
    }

    [Fact]
    public async Task MergeAsync_keeps_an_images_aspect_ratio_when_it_scales_it_onto_the_page()
    {
        var service = new PdfMergeService(_temp);
        var output = Output();

        await service.MergeAsync(
            [MakePdf("a.pdf", 1, "A"), MakeImage("wide.jpg", 800, 400, SKEncodedImageFormat.Jpeg)],
            output);

        using var merged = PdfDocument.Open(output);
        var image = Assert.Single(merged.GetPage(2).GetImages());
        var bounds = image.Bounds;
        var drawnRatio = bounds.Width / bounds.Height;

        Assert.True(
            Math.Abs(drawnRatio - 2.0) < 0.01,
            $"An 800x400 image was drawn at a {drawnRatio:0.000} ratio; stretching it to fill the page distorts it.");
    }

    [Fact]
    public async Task MergeAsync_refuses_a_file_that_is_neither_a_pdf_nor_an_image()
    {
        var service = new PdfMergeService(_temp);
        var notes = Path.Combine(_temp, "notes.txt");
        File.WriteAllText(notes, "just text");

        await Assert.ThrowsAsync<MyPersonalDrive.Services.Localization.LocalizedInvalidOperationException>(
            () => service.MergeAsync([MakePdf("a.pdf", 1, "A"), notes], Output()));
    }

    [Fact]
    public async Task MergeAsync_reports_an_image_whose_bytes_do_not_decode()
    {
        var service = new PdfMergeService(_temp);
        var broken = Path.Combine(_temp, "broken.jpg");
        File.WriteAllText(broken, "not image bytes");

        await Assert.ThrowsAsync<MyPersonalDrive.Services.Localization.LocalizedIOException>(
            () => service.MergeAsync([MakePdf("a.pdf", 1, "A"), broken], Output()));
    }

    [Fact]
    public async Task MergeAsync_leaves_no_temporary_page_files_behind()
    {
        var scratch = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.PdfMerge.Scratch").FullName;
        var service = new PdfMergeService(scratch);

        await service.MergeAsync(
            [MakePdf("a.pdf", 1, "A"), MakeImage("photo.jpg", 200, 200, SKEncodedImageFormat.Jpeg)],
            Output());

        // The per-image pages are whole images on disk, so a leak here is not a few stray bytes.
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Directory.Delete(scratch, recursive: true);
    }

    [Fact]
    public async Task MergeAsync_refuses_a_single_file()
    {
        var service = new PdfMergeService();

        await Assert.ThrowsAsync<MyPersonalDrive.Services.Localization.LocalizedInvalidOperationException>(
            () => service.MergeAsync([MakePdf("a.pdf", 1, "A")], Output()));
    }

    [Fact]
    public async Task MergeAsync_reports_a_missing_source_instead_of_writing_a_partial_file()
    {
        var service = new PdfMergeService();
        var output = Output();

        await Assert.ThrowsAsync<MyPersonalDrive.Services.Localization.LocalizedIOException>(
            () => service.MergeAsync([MakePdf("a.pdf", 1, "A"), Path.Combine(_temp, "gone.pdf")], output));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task MergeAsync_reports_a_source_that_is_not_a_readable_pdf()
    {
        var service = new PdfMergeService();
        var notAPdf = Path.Combine(_temp, "notreally.pdf");
        File.WriteAllText(notAPdf, "this is not a PDF at all");

        await Assert.ThrowsAsync<MyPersonalDrive.Services.Localization.LocalizedIOException>(
            () => service.MergeAsync([MakePdf("a.pdf", 1, "A"), notAPdf], Output()));
    }
}
