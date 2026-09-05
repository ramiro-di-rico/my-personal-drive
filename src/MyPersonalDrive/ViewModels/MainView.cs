namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The window's top-level views. Mutually exclusive by construction, which two independent
/// booleans were not — see <see cref="MainWindowViewModel.ActiveView"/>
/// (docs/PLAN-UX-ROUND-2.md §5).
/// </summary>
public enum MainView
{
    /// <summary>The folder browser. The text/image/PDF viewer is an overlay on this, not a view of its own.</summary>
    Explorer,

    /// <summary>The sync pair list, promoted out of the settings scroll.</summary>
    Sync,

    /// <summary>Preferences, provider connection cards, CLI path and version.</summary>
    Settings
}
