using System.Diagnostics;
using System.Text;

namespace MyPersonalDrive.Services;

public sealed class ProtonDriveCliExecutor : IProtonDriveCliExecutor
{
    private readonly IProtonDriveCliLocator _locator;

    public ProtonDriveCliExecutor(IProtonDriveCliLocator locator)
    {
        _locator = locator;
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    public async Task<string> ExecuteAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var fileName = _locator.Locate();
        var commandText = $"{fileName} {arguments}".Trim();
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
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (PlatformNotSupportedException)
                {
                }
            }
        });

        CommandStarted?.Invoke(this, new CliCommandStartedEventArgs(commandText));

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Proton Drive CLI.");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
            lock (stdout)
            {
                stdout.AppendLine(line);
            }

            CommandOutput?.Invoke(this, new CliCommandOutputEventArgs(line, isError: false));
        }, cancellationToken);

        var stderrTask = ReadStreamAsync(process.StandardError, line =>
        {
            lock (stderr)
            {
                stderr.AppendLine(line);
            }

            CommandOutput?.Invoke(this, new CliCommandOutputEventArgs(line, isError: true));
        }, cancellationToken);

        var exitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask, exitTask);

        if (process.ExitCode != 0)
        {
            var errorText = stderr.Length == 0 ? stdout.ToString() : stderr.ToString();
            CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? $"Command failed with exit code {process.ExitCode}." : errorText);
        }

        CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
        return stdout.ToString();
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }
}
