using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Reads Proton's published CLI release manifest. Behind an interface because this is the app's
/// only outbound network call — tests must be able to exercise the update flow without it.
/// </summary>
public interface ICliReleaseFeed
{
    /// <summary>
    /// The current Stable release, resolved to the file for this machine's platform.
    /// Returns null when the manifest has no Stable entry, or none built for this platform.
    /// </summary>
    Task<CliReleaseCandidate?> GetLatestStableAsync(CancellationToken cancellationToken = default);
}
