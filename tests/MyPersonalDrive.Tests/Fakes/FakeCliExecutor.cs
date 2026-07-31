using MyPersonalDrive.Services;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Records every invocation and returns a canned response, so <see cref="ProtonDriveService"/>
/// (and anything built on top of it) can be tested without a real `proton-drive` process.
/// </summary>
public sealed record RecordedCall(IReadOnlyList<string> Arguments, TimeSpan? Timeout);

public sealed class FakeCliExecutor : IProtonDriveCliExecutor
{
    private readonly Queue<Func<IReadOnlyList<string>, string>> _responses = new();

    public List<RecordedCall> Calls { get; } = [];

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <summary>Queues a fixed stdout response for the next call.</summary>
    public void EnqueueOutput(string stdout) => _responses.Enqueue(_ => stdout);

    /// <summary>Queues a computed stdout response, so a test can assert on the arguments it received.</summary>
    public void EnqueueOutput(Func<IReadOnlyList<string>, string> respond) => _responses.Enqueue(respond);

    /// <summary>Queues a failure the next call should throw.</summary>
    public void EnqueueFailure(CliException exception) => _responses.Enqueue(_ => throw exception);

    public Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        Calls.Add(new RecordedCall(arguments, timeout));
        CommandStarted?.Invoke(this, new CliCommandStartedEventArgs(string.Join(' ', arguments)));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeCliExecutor received a call with no queued response. Call EnqueueOutput/EnqueueFailure first.");
        }

        var respond = _responses.Dequeue();
        var result = respond(arguments);
        CommandFinished?.Invoke(this, new CliCommandFinishedEventArgs(string.Join(' ', arguments), 0));
        return Task.FromResult(result);
    }
}
