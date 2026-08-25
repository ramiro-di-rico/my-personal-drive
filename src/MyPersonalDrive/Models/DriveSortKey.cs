namespace MyPersonalDrive.Models;

/// <summary>
/// What the listing is ordered by (docs/PLAN-BROWSER-VIEWS.md V4). Persisted in
/// <c>settings.json</c> as a string, for the same reason as <see cref="DriveViewMode"/>: an
/// unrecognized value from a future version has to degrade rather than fail to load.
/// </summary>
public enum DriveSortKey
{
    Name,
    Size,
    Modified,
    Kind,
}
