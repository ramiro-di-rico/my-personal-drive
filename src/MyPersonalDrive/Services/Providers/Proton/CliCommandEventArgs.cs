namespace MyPersonalDrive.Services.Providers.Proton;

public sealed class CliCommandStartedEventArgs(string commandText) : EventArgs
{
    public string CommandText { get; } = commandText;
}

public sealed class CliCommandOutputEventArgs(string text, bool isError) : EventArgs
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

public sealed class CliCommandFinishedEventArgs(string commandText, int exitCode) : EventArgs
{
    public string CommandText { get; } = commandText;
    public int ExitCode { get; } = exitCode;
    public bool Succeeded => ExitCode == 0;
}
