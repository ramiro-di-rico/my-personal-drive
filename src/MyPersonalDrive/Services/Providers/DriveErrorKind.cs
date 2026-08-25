namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Coarse classification of a remote drive failure, shared by every provider. Each provider's own
/// classifier (e.g. <c>Providers.Proton.CliErrorClassifier</c>, substring-matching CLI text; a
/// future <c>GraphErrorClassifier</c>, reading HTTP status/error codes) derives one of these so
/// callers switch on a stable enum instead of provider-specific message text
/// (docs/PLAN-CLOUD-PROVIDERS.md §2.6).
/// </summary>
public enum DriveErrorKind
{
    Unknown,
    NotAuthenticated,
    NotFound,
    AlreadyExists,
    Quota,
    Network,
    Timeout,

    /// <summary>
    /// The Proton CLI lost a race against another `proton-drive` process on its own internal
    /// SQLite cache (`SQLITE_BUSY`). Verified reproducible — see docs/PLAN-LOCAL-SYNC.md
    /// Appendix A #11. Nothing is wrong with the request; it just has to be tried again, ideally
    /// without a concurrent sibling.
    /// </summary>
    Busy,

    PermissionDenied,
    InvalidArgument,

    /// <summary>
    /// The provider asked the caller to slow down (Graph 429/503 + <c>Retry-After</c>). Proton's
    /// CLI has no equivalent today — its analogous condition is <see cref="Busy"/>.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The operation collided with the current state of the target (e.g. Graph's <c>fail</c>
    /// conflict behavior returning 409). Distinct from <see cref="AlreadyExists"/>, which is
    /// specifically "a node with this name already exists here" — this covers other
    /// state conflicts a provider might report.
    /// </summary>
    Conflict
}
