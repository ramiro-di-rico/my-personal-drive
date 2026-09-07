namespace MyPersonalDrive.Services;

/// <summary>
/// What the merge action accepts. One place, so the button's visibility, the dialog's list and the
/// service's own guard cannot drift apart — the same reason <see cref="PdfPreviewPolicy.CanPreview"/>
/// is shared between the row's eye button and the viewer.
///
/// Images are in because a page holding a picture is still a page: the merge converts each one into
/// a single-page PDF before joining (docs/ARCHITECTURE.md §7.2). The accepted image formats are
/// exactly <see cref="ImagePreviewPolicy"/>'s — what SkiaSharp decodes — rather than a second,
/// wider list that would only turn a download into an error message.
/// </summary>
public static class PdfMergePolicy
{
    /// <summary>Whether a file can take part in a merge at all.</summary>
    public static bool CanMerge(string name) => IsPdf(name) || IsImage(name);

    /// <summary>Whether the file goes into the merge as-is.</summary>
    public static bool IsPdf(string name) => PdfPreviewPolicy.IsPdfName(name);

    /// <summary>Whether the file has to become a page first.</summary>
    public static bool IsImage(string name) => ImagePreviewPolicy.IsSupportedImageName(name);
}
