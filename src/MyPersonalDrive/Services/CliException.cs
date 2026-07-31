namespace MyPersonalDrive.Services;

/// <summary>
/// Raised when a Proton Drive CLI invocation fails. Inherits from
/// <see cref="InvalidOperationException"/> so existing call sites that catch that base type
/// keep working unchanged; new code should catch <see cref="CliException"/> and switch on
/// <see cref="Kind"/> instead of inspecting <see cref="Exception.Message"/>.
/// </summary>
public class CliException : InvalidOperationException
{
    public CliException(string commandText, int exitCode, string stdout, string stderr, string message, CliErrorKind kind = CliErrorKind.Unknown)
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

    public CliErrorKind Kind { get; init; }
}
