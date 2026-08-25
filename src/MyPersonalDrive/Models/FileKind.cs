namespace MyPersonalDrive.Models;

/// <summary>
/// What a node is, for the two consumers that need it: the listing's icon (view modes) and the
/// per-directory type histogram (metrics). See docs/PLAN-BROWSER-VIEWS.md V3/M1.
/// </summary>
public enum FileKind
{
    Folder,
    Image,
    Video,
    Audio,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Archive,
    Code,
    Text,
    Other,
}
