namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// Raised when a remote drive operation fails, whatever the provider's transport underneath —
/// a CLI process exit code today, an HTTP status once a second provider lands. Inherits from
/// <see cref="InvalidOperationException"/> so existing call sites that catch that base type
/// keep working unchanged; new code should catch <see cref="DriveException"/> and switch on
/// <see cref="Kind"/> instead of inspecting <see cref="Exception.Message"/>.
///
/// <see cref="CommandText"/>/<see cref="Stdout"/>/<see cref="Stderr"/> are named for the
/// process-based transport that is still the only one that exists; a provider with no process
/// (Microsoft Graph, see docs/PLAN-CLOUD-PROVIDERS.md P6) would populate them with its request
/// description and response body instead of leaving the shape provider-specific.
/// </summary>
public class DriveException : InvalidOperationException, Localization.ILocalizedError
{
    public DriveException(string commandText, int exitCode, string stdout, string stderr, string message, DriveErrorKind kind = DriveErrorKind.Unknown)
        : base(message)
    {
        CommandText = commandText;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        Kind = kind;
    }

    public string CommandText { get; }

    public int ExitCode { get; }

    public string Stdout { get; }

    public string Stderr { get; }

    public DriveErrorKind Kind { get; init; }

    /// <summary>
    /// This app's own sentence about the failure, translatable, when the failure is one we can
    /// describe rather than one we are quoting. Empty when <see cref="Exception.Message"/> is the
    /// provider's own words — those are shown verbatim (docs/PLAN-I18N.md §9, PLAN-TECH-DEBT.md
    /// B6.5). <see cref="Exception.Message"/> stays English either way, because it is what reaches
    /// the CLI console and the crash log.
    /// </summary>
    public Localization.LocalizedText Detail { get; init; }
}
