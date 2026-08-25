using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Records every invocation and returns a canned response, so <see cref="ProtonDriveService"/>
/// (and anything built on top of it) can be tested without a real `proton-drive` process.
/// </summary>
public sealed record RecordedCall(IReadOnlyList<string> Arguments, TimeSpan? Timeout);

public sealed class FakeCliExecutor : IProtonDriveCliExecutor
{
    private readonly Queue<Func<IReadOnlyList<string>, string>> _responses = new();
    private readonly Dictionary<string, string> _responsesByLastArgument = new();
    private readonly object _lock = new();

    public List<RecordedCall> Calls { get; } = [];

    /// <summary>How many times the remote cache was discarded, so a test can assert a scan asked for a fresh view.</summary>
    public int RemoteCacheResets { get; private set; }

    public Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            RemoteCacheResets++;
        }

        return Task.CompletedTask;
    }

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <summary>Queues a fixed stdout response for the next call.</summary>
    public void EnqueueOutput(string stdout) => _responses.Enqueue(_ => stdout);

    /// <summary>Queues a computed stdout response, so a test can assert on the arguments it received.</summary>
    public void EnqueueOutput(Func<IReadOnlyList<string>, string> respond) => _responses.Enqueue(respond);

    /// <summary>Queues a failure the next call should throw.</summary>
    public void EnqueueFailure(DriveException exception) => _responses.Enqueue(_ => throw exception);

    /// <summary>
    /// Routes by the call's last argument (the target path, for every command this app issues)
    /// instead of call order — needed for BFS-style scanners that make several concurrent
    /// calls whose relative order isn't deterministic. Takes priority over the queue.
    /// </summary>
    public void RespondForPath(string path, string stdout) => _responsesByLastArgument[path] = stdout;

    public Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        lock (_lock)
        {
            Calls.Add(new RecordedCall(arguments, timeout));
        }

        CommandStarted?.Invoke(this, new CliCommandStartedEventArgs(string.Join(' ', arguments)));

        string result;
        if (_responsesByLastArgument.Count > 0 && arguments.Count > 0 && _responsesByLastArgument.TryGetValue(arguments[^1], out var byPath))
        {
            result = byPath;
        }
        else
        {
            Func<IReadOnlyList<string>, string> respond;
            lock (_lock)
            {
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException($"FakeCliExecutor received a call ({string.Join(' ', arguments)}) with no matching response. Call EnqueueOutput/RespondForPath first.");
                }

                respond = _responses.Dequeue();
            }

            result = respond(arguments);
        }

        CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(string.Join(' ', arguments), 0));
        return Task.FromResult(result);
    }
}
