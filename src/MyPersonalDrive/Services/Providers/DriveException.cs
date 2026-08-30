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
public class DriveException : InvalidOperationException
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
}
