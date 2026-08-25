namespace MyPersonalDrive.Services;

public sealed class AppSettings
{
    public string CliPath { get; set; } = string.Empty;

    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// When true, a `filesystem list --json` response that isn't valid JSON is treated as a
    /// hard error instead of falling back to a best-effort text parser. Defaults to false
    /// until docs/PLAN-LOCAL-SYNC.md Phase 0 confirms the CLI reliably honors --json, at
    /// which point the default should flip to true.
    /// </summary>
    public bool StrictListingParsing { get; set; }

    /// <summary>
    /// The folder listing's presentation, as the name of a <see cref="Models.DriveViewMode"/>.
    /// Stored as a string on purpose: an enum-typed property changes the set of converters
    /// <c>AppJsonContext</c> generates, and a value written by a newer version has to degrade to
    /// the default rather than throw. Read it through <see cref="ViewModeOrDefault"/>.
    /// </summary>
    public string ViewMode { get; set; } = nameof(Models.DriveViewMode.List);

    /// <summary>The listing's sort key, as a <see cref="Models.DriveSortKey"/> name.</summary>
    public string SortKey { get; set; } = nameof(Models.DriveSortKey.Name);

    public bool SortDescending { get; set; }

    public Models.DriveSortKey SortKeyOrDefault()
        => Enum.TryParse<Models.DriveSortKey>(SortKey, ignoreCase: true, out var key)
            ? key
            : Models.DriveSortKey.Name;

    public Models.DriveViewMode ViewModeOrDefault()
        => Enum.TryParse<Models.DriveViewMode>(ViewMode, ignoreCase: true, out var mode)
            ? mode
            : Models.DriveViewMode.List;
}
