namespace MyPersonalDrive.Services;

public sealed class AppSettings
{
    public string CliPath { get; set; } = string.Empty;

    public bool IsAuthenticated { get; set; }

    public string ProtonAccountLabel { get; set; } = string.Empty;

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

    public string OneDriveAccountLabel { get; set; } = string.Empty;

    /// <summary>
    /// The Google Cloud Console OAuth client's (Desktop app) client id, entered in Settings — same
    /// reasoning as <see cref="OneDriveClientId"/>. See docs/PLAN-CLOUD-PROVIDERS.md §8.1/P10.
    /// </summary>
    public string GoogleDriveClientId { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth client's "secret" — Google still issues one for a Desktop-app client even though
    /// it isn't required to be kept truly confidential for this client type (it ships inside a
    /// downloaded, publicly-distributable app the same way <see cref="OneDriveClientId"/>'s
    /// counterpart does not need one at all). Stored in plaintext here, same accepted-risk shape as
    /// the token store itself (docs/PLAN-CLOUD-PROVIDERS.md §4.2/R3, §8.1) — not a real secret in
    /// the way a web-app client secret would be.
    /// </summary>
    public string GoogleDriveClientSecret { get; set; } = string.Empty;

    public bool IsGoogleDriveAuthenticated { get; set; }

    public string GoogleDriveAccountLabel { get; set; } = string.Empty;

    public bool IsNextcloudAuthenticated { get; set; }

    public string NextcloudAccountLabel { get; set; } = string.Empty;

    public bool IsS3Authenticated { get; set; }

    public string S3AccountLabel { get; set; } = string.Empty;

    /// <summary>
    /// The user's chosen workspace theme variant: "Default" (System default), "Light", or "Dark".
    /// </summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// The interface language, as a <see cref="Localization.Language.Code"/> ("en", "es"). Same
    /// string-not-enum reasoning as <see cref="ActiveProvider"/>: a code written by a newer build
    /// must degrade to English rather than throw at startup. Read it through
    /// <see cref="LanguageOrDefault"/>.
    ///
    /// The default is English, and there is deliberately no migration for a settings file written
    /// before this field existed — such a file deserializes to "en" and the interface switches
    /// language once, which is a visit to Settings rather than a one-shot migration branch that
    /// would outlive its usefulness (docs/PLAN-I18N.md §2.6, option A).
    /// </summary>
    public string Language { get; set; } = Localization.LanguageCatalog.DefaultCode;

    public string LanguageOrDefault() => Localization.LanguageCatalog.ResolveOrDefault(Language).Code;

    /// <summary>
    /// Network bandwidth throttle limit in KB/s (0 = unlimited).
    /// </summary>
    public int BandwidthLimitKbps { get; set; }

    /// <summary>
    /// Default local folder path for file sync pairs and downloads.
    /// </summary>
    public string DefaultSyncFolder { get; set; } = string.Empty;

    /// <summary>Whether the local pane's browser shows dotfiles/hidden entries. See docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3.</summary>
    public bool ShowHiddenLocalFiles { get; set; }

    /// <summary>Whether the right-hand Status/Metrics sidebar is shown. Toggled from the header; the persisted value is also next launch's default.</summary>
    public bool ShowStatusPanel { get; set; } = true;

    /// <summary>Whether the local filesystem pane is expanded. Toggled from the header; the persisted value is also next launch's default.</summary>
    public bool ShowLocalExplorerPanel { get; set; } = true;

    /// <summary>Whether the bottom CLI activity panel is expanded. See docs/INTERFACE_IMPROVEMENT_PLAN.md Task 4.</summary>
    public bool ShowCommandConsole { get; set; } = true;

    /// <summary>
    /// The in-app viewer's display scale for images and PDF pages (1.0 = the decoded bitmap's own
    /// pixel size). Defaults to 0.5 rather than 1.0: a PDF page or a modern photo is routinely
    /// bigger than the viewer panel, and showing it at full resolution meant scrolling to see any
    /// of it. Read through <see cref="ViewerZoomOrDefault"/>, which clamps a corrupt or
    /// out-of-range value rather than handing the view something that would make the content
    /// vanish (0) or become unusable (a huge multiple).
    /// </summary>
    public double ViewerZoom { get; set; } = 0.5;

    public const double MinViewerZoom = 0.25;
    public const double MaxViewerZoom = 1.5;

    public double ViewerZoomOrDefault()
        => double.IsFinite(ViewerZoom) ? Math.Clamp(ViewerZoom, MinViewerZoom, MaxViewerZoom) : 0.5;

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

    /// <summary>
    /// The single place that knows which of the per-provider bool/label field pairs above backs a
    /// given <see cref="Providers.ProviderId"/> — callers (the header's provider list, sign-in/out,
    /// account switching) all need the same mapping and used to each re-derive it independently.
    /// </summary>
    public bool IsProviderAuthenticated(Providers.ProviderId id) => id switch
    {
        Providers.ProviderId.OneDrive => IsOneDriveAuthenticated,
        Providers.ProviderId.GoogleDrive => IsGoogleDriveAuthenticated,
        Providers.ProviderId.Nextcloud => IsNextcloudAuthenticated,
        Providers.ProviderId.S3 => IsS3Authenticated,
        _ => IsAuthenticated
    };

    public void SetProviderAuthenticated(Providers.ProviderId id, bool value)
    {
        switch (id)
        {
            case Providers.ProviderId.OneDrive: IsOneDriveAuthenticated = value; break;
            case Providers.ProviderId.GoogleDrive: IsGoogleDriveAuthenticated = value; break;
            case Providers.ProviderId.Nextcloud: IsNextcloudAuthenticated = value; break;
            case Providers.ProviderId.S3: IsS3Authenticated = value; break;
            default: IsAuthenticated = value; break;
        }
    }

    public string ProviderAccountLabel(Providers.ProviderId id) => id switch
    {
        Providers.ProviderId.OneDrive => OneDriveAccountLabel,
        Providers.ProviderId.GoogleDrive => GoogleDriveAccountLabel,
        Providers.ProviderId.Nextcloud => NextcloudAccountLabel,
        Providers.ProviderId.S3 => S3AccountLabel,
        _ => ProtonAccountLabel
    };

    public void SetProviderAccountLabel(Providers.ProviderId id, string label)
    {
        switch (id)
        {
            case Providers.ProviderId.OneDrive: OneDriveAccountLabel = label; break;
            case Providers.ProviderId.GoogleDrive: GoogleDriveAccountLabel = label; break;
            case Providers.ProviderId.Nextcloud: NextcloudAccountLabel = label; break;
            case Providers.ProviderId.S3: S3AccountLabel = label; break;
            default: ProtonAccountLabel = label; break;
        }
    }
}
