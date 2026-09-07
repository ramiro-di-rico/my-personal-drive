namespace MyPersonalDrive.Models;

/// <summary>
/// What the merge dialog hands back: which of the selected PDFs to join, in which order, and what
/// to call the result.
///
/// <see cref="SourceOrder"/> holds indexes into the list the dialog was given, not names — two
/// files selected across a listing can share a name, and an index cannot be ambiguous the way a
/// name can. It may be shorter than that list: the dialog lets a file be dropped from the merge
/// without clearing and redoing the selection.
/// </summary>
/// <param name="SourceOrder">Indexes into the caller's list, in the page order chosen.</param>
/// <param name="OutputName">The file name for the merged PDF, extension included.</param>
public sealed record PdfMergeRequest(IReadOnlyList<int> SourceOrder, string OutputName);
