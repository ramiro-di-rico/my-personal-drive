namespace MyPersonalDrive.Services;

public interface IProtonDriveCliExecutor
{
    event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <param name="arguments">
    /// Each element becomes one process argument via <c>ProcessStartInfo.ArgumentList</c>,
    /// which lets the runtime apply platform-correct escaping. Never build a single
    /// pre-quoted string: it cannot round-trip names containing quotes or backslashes.
    /// </param>
    /// <param name="timeout">
    /// Null uses the executor's default timeout. Pass <see cref="Timeout.InfiniteTimeSpan"/>
    /// for commands that wait on user interaction (e.g. a browser-based login).
    /// </param>
    Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null);
}
