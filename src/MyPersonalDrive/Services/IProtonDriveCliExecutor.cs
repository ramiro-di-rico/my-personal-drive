namespace MyPersonalDrive.Services;

public interface IProtonDriveCliExecutor
{
    event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    Task<string> ExecuteAsync(string arguments, CancellationToken cancellationToken = default);
}
