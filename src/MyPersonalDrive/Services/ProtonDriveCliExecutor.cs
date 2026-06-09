using System.Diagnostics;

namespace MyPersonalDrive.Services;

public sealed class ProtonDriveCliExecutor : IProtonDriveCliExecutor
{
    private readonly IProtonDriveCliLocator _locator;

    public ProtonDriveCliExecutor(IProtonDriveCliLocator locator)
    {
        _locator = locator;
    }

    public async Task<string> ExecuteAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var fileName = _locator.Locate();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Proton Drive CLI.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask, exitTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }

        return stdout;
    }
}
