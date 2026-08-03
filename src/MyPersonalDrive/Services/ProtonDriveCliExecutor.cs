using System.Diagnostics;
using System.Text;

namespace MyPersonalDrive.Services;

public sealed class ProtonDriveCliExecutor : IProtonDriveCliExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IProtonDriveCliLocator _locator;

    /// <summary>
    /// docs/PLAN-LOCAL-SYNC.md §9's global gate over `proton-drive` processes. <b>This is a
    /// correctness requirement, not a throttle.</b> Appendix A #11 measured that concurrent CLI
    /// processes crash each other on the CLI's own internal SQLite cache roughly one time in three,
    /// so two overlapping invocations corrupt work rather than merely slowing it down.
    ///
    /// Three independent producers exist — the scheduler's automatic cycles, the sync panel's
    /// "Sync now"/"Preview", and the file browser — and none of them can see the others. Gating here
    /// is what makes that safe: every CLI invocation in the app funnels through this method, so one
    /// semaphore covers all three, which no amount of coordination between them would.
    ///
    /// Held per *invocation* (~3.5s), never per sync cycle, so an interactive click waits behind at
    /// most one command instead of a whole scan. That's why §9's "give priority to interactive
    /// operations" isn't implemented yet: with this granularity the worst-case wait is already
    /// short. `auth login` is the one long holder, and blocking sync behind it is correct — nothing
    /// can succeed without a session anyway.
    /// </summary>
    private readonly SemaphoreSlim _oneProcessAtATime = new(1, 1);

    public ProtonDriveCliExecutor(IProtonDriveCliLocator locator)
    {
        _locator = locator;
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        await _oneProcessAtATime.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteSerializedAsync(arguments, cancellationToken, timeout);
        }
        finally
        {
            _oneProcessAtATime.Release();
        }
    }

    private async Task<string> ExecuteSerializedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var fileName = _locator.Locate();
        var commandText = FormatCommandText(fileName, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var timeoutCts = CreateTimeoutCts(timeout);
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var effectiveToken = linkedCts?.Token ?? cancellationToken;

        using var cancellationRegistration = effectiveToken.Register(() =>
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
        }, effectiveToken);

        var stderrTask = ReadStreamAsync(process.StandardError, line =>
        {
            lock (stderr)
            {
                stderr.AppendLine(line);
            }

            CommandOutput?.Invoke(this, new CliCommandOutputEventArgs(line, isError: true));
        }, effectiveToken);

        try
        {
            var exitTask = process.WaitForExitAsync(effectiveToken);
            await Task.WhenAll(stdoutTask, stderrTask, exitTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
        {
            CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, -1));
            throw new CliException(commandText, -1, stdout.ToString(), stderr.ToString(),
                $"Command timed out after {timeout ?? DefaultTimeout}.", CliErrorKind.Timeout);
        }

        if (process.ExitCode != 0)
        {
            var stdoutText = stdout.ToString();
            var stderrText = stderr.ToString();
            // An internal CLI crash puts a bare `====` banner on stderr and the real diagnosis on
            // stdout, so "stderr if non-empty" produced exceptions whose entire message was
            // `===============`. Fall through to stdout whenever stderr carries no actual words.
            var errorText = HasContent(stderrText) ? stderrText : stdoutText;
            var kind = CliErrorClassifier.Classify(process.ExitCode, stdoutText, stderrText);
            CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
            throw new CliException(
                commandText,
                process.ExitCode,
                stdoutText,
                stderrText,
                string.IsNullOrWhiteSpace(errorText) ? $"Command failed with exit code {process.ExitCode}." : errorText,
                kind);
        }

        CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(commandText, process.ExitCode));
        return stdout.ToString();
    }

    private static CancellationTokenSource? CreateTimeoutCts(TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(effectiveTimeout);
        return cts;
    }

    private static string FormatCommandText(string fileName, IReadOnlyList<string> arguments)
    {
        // Presentation only (shown in the activity console); never fed back into execution.
        var quoted = arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
        return $"{fileName} {string.Join(' ', quoted)}".Trim();
    }

    /// <summary>
    /// Whether a captured stream holds anything a human could act on. The CLI's crash banner is
    /// pure `=` and whitespace, which is non-empty but says nothing.
    /// </summary>
    private static bool HasContent(string text)
        => text.AsSpan().TrimStart().TrimStart('=').TrimStart().Length > 0;

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
