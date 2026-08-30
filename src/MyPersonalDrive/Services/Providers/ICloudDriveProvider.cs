namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// The one thing the rest of the app depends on for talking to a cloud drive. A facade over the
/// operations, auth and two optional capabilities that not every backend has — see
/// docs/PLAN-CLOUD-PROVIDERS.md §2.1 for why this stays a facade instead of one flat interface.
/// </summary>
public interface ICloudDriveProvider
{
    ProviderId Id { get; }

    string DisplayName { get; }

    ProviderCapabilities Capabilities { get; }

    IDriveOperations Operations { get; }

    IDriveAuthenticator Auth { get; }

    IProviderPathSyntax Paths { get; }

    /// <summary>Null when the provider has no stale-cache problem to invalidate.</summary>
    IRemoteViewInvalidator? RemoteView { get; }

    /// <summary>Null when there is no external binary to version or update.</summary>
    IProviderDiagnostics? Diagnostics { get; }

    /// <summary>Null when the backend has no delta/changes query of its own (Proton's CLI). See <see cref="IDeltaSource"/>, docs/PLAN-CLOUD-PROVIDERS.md P8.</summary>
    IDeltaSource? DeltaSource { get; }

    /// <summary>The activity console feed — see <see cref="ProviderActivity"/>.</summary>
    event EventHandler<ProviderActivity>? Activity;

    /// <summary>See <c>Providers.Proton.ProtonDriveService.ListingParseWarning</c>.</summary>
    event EventHandler<string>? ListingParseWarning;
}
