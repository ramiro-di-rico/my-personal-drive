namespace MyPersonalDrive.Models;

/// <summary>One row of the "Properties" dialog (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6).</summary>
/// <param name="IsCopyable">
/// Whether the row gets a copy-to-clipboard button. Set for paths, which are the only values here
/// anyone needs to paste somewhere else — and which are also the ones too long to retype
/// (docs/PLAN-UX-ROUND-2.md §12). Defaults to false so every existing field is unaffected.
/// </param>
public sealed record PropertyField(string Label, string Value, bool IsCopyable = false);
