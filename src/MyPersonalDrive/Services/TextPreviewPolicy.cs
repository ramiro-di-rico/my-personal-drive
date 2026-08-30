using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Decides which nodes the in-app text viewer offers itself for, and how much of one it will read.
/// Pure and static — the row (for the eye button's visibility) and the viewer service (for its
/// limits) both go through here, so "previewable" means one thing.
///
/// Deliberately conservative on both ends: the extension has to suggest text, and the file has to
/// be small, because previewing costs a full CLI download of the file. Anything that slips through
/// anyway (an extensionless file that turns out to be a JPEG) is caught by the binary sniff at read
/// time, not here.
/// </summary>
public static class TextPreviewPolicy
{
    /// <summary>Most bytes read from a previewed file. Past this the viewer truncates.</summary>
    public const long MaxPreviewBytes = 1024 * 1024;

    /// <summary>Most lines shown. A one-line 900 KB JSON blob is still bounded by the byte limit.</summary>
    public const int MaxPreviewLines = 5000;

    /// <summary>
    /// Whether the viewer should offer itself for <paramref name="item"/>: a file, plausibly text
    /// by name, and small enough to be worth downloading. A file whose size the listing didn't
    /// report is allowed through — the reader truncates if it turns out to be huge.
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

        return IsTextName(item.Name);
    }

    /// <summary>
    /// Whether a name suggests plain text. Text and code kinds qualify outright; of the spreadsheet
    /// kinds only the delimited ones do (".xlsx" is a zip, ".csv" is text); "Other" — no extension
    /// at all, like LICENSE or Makefile — is allowed because those are usually text, and the binary
    /// sniff covers the times they aren't.
    /// </summary>
    public static bool IsTextName(string name)
    {
        var kind = FileKindClassifier.Classify(name, isFolder: false);
        return kind switch
        {
            FileKind.Text or FileKind.Code or FileKind.Other => true,
            FileKind.Spreadsheet => name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
