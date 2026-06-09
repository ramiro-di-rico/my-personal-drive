namespace MyPersonalDrive.Services;

public interface IProtonDriveCliExecutor
{
    Task<string> ExecuteAsync(string arguments, CancellationToken cancellationToken = default);
}
