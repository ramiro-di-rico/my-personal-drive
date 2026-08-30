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

    /// <summary>
    /// The user's chosen provider, as the name of a <see cref="Providers.ProviderId"/>. Same
    /// string-not-enum reasoning as <see cref="ViewMode"/>, plus one more: a value naming a
    /// provider this build can't construct (an older provider removed, or a settings file from a
    /// newer build) must degrade to Proton rather than throw at startup. Read it through
    /// <see cref="ActiveProviderOrDefault"/>. See docs/PLAN-CLOUD-PROVIDERS.md P5.
    /// </summary>
    public string ActiveProvider { get; set; } = nameof(Providers.ProviderId.Proton);

    /// <summary>
    /// The Azure app registration's (public client) application id, entered in Settings rather
    /// than embedded in the binary — kept separate from Proton's fields entirely rather than
    /// generalized into a provider-keyed settings shape: OneDrive's connection card (sign-in/out +
    /// account label) has no field this could double up with. See docs/PLAN-CLOUD-PROVIDERS.md P6.
    /// </summary>
    public string OneDriveClientId { get; set; } = string.Empty;

    /// <summary>Cached hint mirroring <see cref="IsAuthenticated"/> for OneDrive — the actual token lives in <c>onedrive-token.json</c>, not here.</summary>
    public bool IsOneDriveAuthenticated { get; set; }

    /// <summary>
    /// The user's chosen workspace theme variant: "Default" (System default), "Light", or "Dark".
    /// </summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// Network bandwidth throttle limit in KB/s (0 = unlimited).
    /// </summary>
    public int BandwidthLimitKbps { get; set; }

    /// <summary>
    /// Default local folder path for file sync pairs and downloads.
    /// </summary>
    public string DefaultSyncFolder { get; set; } = string.Empty;

    public string ThemeOrDefault()
        => string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
         : string.Equals(Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
         : "Default";

    public Models.DriveSortKey SortKeyOrDefault()
        => Enum.TryParse<Models.DriveSortKey>(SortKey, ignoreCase: true, out var key)
            ? key
            : Models.DriveSortKey.Name;

    public Models.DriveViewMode ViewModeOrDefault()
        => Enum.TryParse<Models.DriveViewMode>(ViewMode, ignoreCase: true, out var mode)
            ? mode
            : Models.DriveViewMode.List;

    public Providers.ProviderId ActiveProviderOrDefault()
        => Enum.TryParse<Providers.ProviderId>(ActiveProvider, ignoreCase: true, out var id)
            ? id
            : Providers.ProviderId.Proton;
}
