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
    PermissionDenied,
    InvalidArgument
}
