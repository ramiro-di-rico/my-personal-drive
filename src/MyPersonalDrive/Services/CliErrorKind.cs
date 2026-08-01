namespace MyPersonalDrive.Services;

/// <summary>
/// Coarse classification of a Proton Drive CLI failure. The CLI does not (yet) expose
/// distinct exit codes per failure type, so <see cref="CliErrorClassifier"/> derives this from
/// message text. Callers should switch on this enum rather than inspecting message text
/// themselves, so that the substring matching stays isolated to one place.
/// </summary>
public enum CliErrorKind
{
    Unknown,
    NotAuthenticated,
    NotFound,
    AlreadyExists,
    Quota,
    Network,
    Timeout,

    /// <summary>
    /// The CLI lost a race against another `proton-drive` process on its own internal SQLite
    /// cache (`SQLITE_BUSY`). Verified reproducible — see docs/PLAN-LOCAL-SYNC.md Appendix A #11.
    /// Nothing is wrong with the request; it just has to be tried again, ideally without a
    /// concurrent sibling.
    /// </summary>
    Busy,

    PermissionDenied,
    InvalidArgument
}
