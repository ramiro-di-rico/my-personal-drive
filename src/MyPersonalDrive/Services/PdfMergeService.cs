using MyPersonalDrive.Services.Localization;
using UglyToad.PdfPig.Writer;

namespace MyPersonalDrive.Services;

/// <summary>
/// Joins several local files into one PDF, in the order given. Kept behind an interface so
/// view-model tests can assert on the merge that was requested without touching PDFium, PdfPig or
/// the filesystem — the same seam <see cref="IPdfFilePreviewLoader"/> uses.
/// </summary>
public interface IPdfMergeService
{
    /// <summary>
    /// Merges <paramref name="localPaths"/> into <paramref name="outputPath"/>, page order
    /// following list order. Each entry is either a PDF or an image
    /// (<see cref="PdfMergePolicy.CanMerge"/>). Returns the number of pages written.
    /// </summary>
    Task<int> MergeAsync(IReadOnlyList<string> localPaths, string outputPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real merger, over PdfPig's <see cref="PdfMerger"/>. Note what this deliberately is not:
/// PDFium (via the PDFtoImage package the preview uses) can only rasterize, so merging through it
/// would replace every page with a picture of itself and throw the text layer away. PdfPig
/// re-writes the page objects, so the output keeps its text, links and bookmarks.
///
/// Images are handled by converting each one to a single-page PDF first
/// (<see cref="ImageToPdfPageConverter"/>) and then merging as usual, rather than by assembling the
/// output page by page in a <see cref="PdfDocumentBuilder"/>. Both work — this one keeps the
/// PDF-to-PDF path, the one with the most to lose, exactly as it was before images existed.
/// </summary>
public sealed class PdfMergeService : IPdfMergeService
{
    private readonly string _tempRoot;

    public PdfMergeService(string? tempRoot = null)
        => _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "MyPersonalDrive", "merge-pages");

    public Task<int> MergeAsync(IReadOnlyList<string> localPaths, string outputPath, CancellationToken cancellationToken = default)
    {
        if (localPaths.Count < 2)
        {
            throw new LocalizedInvalidOperationException(
                "Merging needs at least two files.",
                LocalizedText.Of(StringKeys.Error.PdfMergeNeedsTwoFiles));
        }

        // PdfPig is synchronous and CPU-bound: read, parse and re-write every page object, plus a
        // decode and re-encode per image. Running it inline would block whichever thread the
        // AsyncCommand happens to be on — the UI thread for a command invoked from a button.
        return Task.Run(
            () =>
            {
                foreach (var path in localPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(path))
                    {
                        throw new LocalizedIOException(
                            $"'{path}' is not there to merge.",
                            LocalizedText.Of(StringKeys.Error.PdfMergeSourceMissing, Path.GetFileName(path)));
                    }

                    if (!PdfMergePolicy.CanMerge(path))
                    {
                        throw new LocalizedInvalidOperationException(
                            $"'{path}' is neither a PDF nor a supported image.",
                            LocalizedText.Of(StringKeys.Error.PdfMergeUnsupportedKind, Path.GetFileName(path)));
                    }
                }

                // Only created when an image is actually in the selection, so a PDF-only merge
                // touches the filesystem exactly as much as it did before.
                var scratch = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
                try
                {
                    var pdfPaths = new List<string>(localPaths.Count);
                    for (var i = 0; i < localPaths.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = localPaths[i];

                        if (PdfMergePolicy.IsPdf(path))
                        {
                            pdfPaths.Add(path);
                            continue;
                        }

                        Directory.CreateDirectory(scratch);
                        // Named by position, not by the source's name: two selected images can
                        // share one, and the second would overwrite the first's page.
                        var page = Path.Combine(scratch, $"{i}.pdf");
                        ImageToPdfPageConverter.Convert(path, page);
                        pdfPaths.Add(page);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] merged;
                    try
                    {
                        merged = PdfMerger.Merge([.. pdfPaths]);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // PdfPig throws its own exception types for a corrupt or encrypted source,
                        // and there is no typed catalog to switch on the way CliErrorKind lets us.
                        // One user-facing message for "this file is not something we can read" is
                        // honest; matching on its message text would not be (AGENTS.md).
                        throw new LocalizedIOException(
                            $"PdfPig could not merge the selected files: {ex.Message}",
                            LocalizedText.Of(StringKeys.Error.PdfMergeUnreadableSource));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllBytes(outputPath, merged);

                    return PageCountOf(outputPath);
                }
                finally
                {
                    TryDelete(scratch);
                }
            },
            cancellationToken);
    }

    /// <summary>Reads back what was actually written, rather than trusting a sum of the inputs.</summary>
    private static int PageCountOf(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return document.NumberOfPages;
    }

    /// <summary>Best-effort cleanup of the per-image pages. A leftover temp folder is not worth failing a merge that already succeeded.</summary>
    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
