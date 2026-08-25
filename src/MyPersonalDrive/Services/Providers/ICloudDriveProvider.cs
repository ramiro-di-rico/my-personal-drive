using MyPersonalDrive.Services;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// The one thing the rest of the app depends on for talking to a cloud drive. A facade over the
/// operations, auth and two optional capabilities that not every backend has — see
/// docs/PLAN-CLOUD-PROVIDERS.md §2.1 for why this stays a facade instead of one flat interface.
///
/// The console/activity events keep today's <c>Cli*EventArgs</c> shape and <c>ListingParseWarning</c>
/// on purpose: generalizing them into a provider-neutral activity feed is P2's job
/// (docs/PLAN-CLOUD-PROVIDERS.md §2.6), not P1's. P1 only relocates the boundary.
/// </summary>
public interface ICloudDriveProvider
{
    ProviderId Id { get; }

    string DisplayName { get; }

    ProviderCapabilities Capabilities { get; }

    IDriveOperations Operations { get; }

    IDriveAuthenticator Auth { get; }

    /// <summary>Null when the provider has no stale-cache problem to invalidate.</summary>
    IRemoteViewInvalidator? RemoteView { get; }

    /// <summary>Null when there is no external binary to version or update.</summary>
    IProviderDiagnostics? Diagnostics { get; }

    event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

    /// <summary>See <c>Providers.Proton.ProtonDriveService.ListingParseWarning</c>.</summary>
    event EventHandler<string>? ListingParseWarning;
}
