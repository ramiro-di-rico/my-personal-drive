using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Stands in for Proton's release manifest so the update flow can be tested without the app's one
/// outbound network call.
/// </summary>
public sealed class FakeCliReleaseFeed : ICliReleaseFeed
{
    private readonly CliReleaseCandidate? _candidate;
    private readonly Exception? _failure;

    public FakeCliReleaseFeed(CliReleaseCandidate? candidate = null, Exception? failure = null)
    {
        _candidate = candidate;
        _failure = failure;
    }

    public int Calls { get; private set; }

    public Task<CliReleaseCandidate?> GetLatestStableAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return _failure is not null
            ? Task.FromException<CliReleaseCandidate?>(_failure)
            : Task.FromResult(_candidate);
    }
}
