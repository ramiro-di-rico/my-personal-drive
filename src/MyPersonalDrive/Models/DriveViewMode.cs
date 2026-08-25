namespace MyPersonalDrive.Models;

/// <summary>
/// How the folder listing is presented. A user choice, persisted in <c>settings.json</c> as a
/// string (see <c>AppSettings.ViewMode</c>) so an unrecognized value from a future version can
/// degrade to <see cref="List"/> instead of failing to deserialize.
/// </summary>
public enum DriveViewMode
{
    /// <summary>One row per item, with the name, size and the per-row action buttons.</summary>
    List,

    /// <summary>A dense grid of small tiles: icon plus name, actions in a context menu.</summary>
    Icons,

    /// <summary>A grid of large tiles: big icon, name and a metadata line. Not image previews.</summary>
    Gallery,
}
