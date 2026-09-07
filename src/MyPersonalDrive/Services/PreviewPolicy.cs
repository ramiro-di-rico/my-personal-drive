using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Whether an item has a preview at all — the union of the three format policies, minus the
/// provider-side documents that have no bytes to read.
///
/// Extracted because two callers need the same answer and had it in one of them: the row's preview
/// button, and the open gesture (double click, Enter). While the rule lived only in
/// <c>DriveNodeViewModel</c>'s constructor, opening a previewable file did nothing at all
/// (docs/PLAN-UX-ROUND-4.md Y1).
/// </summary>
public static class PreviewPolicy
{
    /// <param name="item">The listing row's item.</param>
    public static bool CanPreview(DriveItem item)
        // A Google-native Doc/Sheet/Slide has no extension (so TextPreviewPolicy's "no extension at
        // all" fallback would otherwise offer to preview it as plain text) and no binary content to
        // actually read — the P10 live-verification pass hit exactly this: the preview button showed
        // up and failed instead of never appearing (docs/PLAN-CLOUD-PROVIDERS.md §8.4/G4).
        => !item.IsRemoteOnlyDocument
            && (TextPreviewPolicy.CanPreview(item) || ImagePreviewPolicy.CanPreview(item) || PdfPreviewPolicy.CanPreview(item));
}
