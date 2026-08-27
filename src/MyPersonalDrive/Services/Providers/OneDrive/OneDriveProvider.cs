using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Adapts the Graph pieces (<see cref="GraphAuthenticator"/>, <see cref="GraphHttpClient"/>,
/// <see cref="OneDriveOperations"/>, <see cref="OneDrivePathSyntax"/>) to
/// <see cref="ICloudDriveProvider"/> — the OneDrive counterpart to
/// <c>Providers.Proton.ProtonDriveProvider</c>. See docs/PLAN-CLOUD-PROVIDERS.md P6.
/// </summary>
public sealed class OneDriveProvider : ICloudDriveProvider, IDisposable
{
    private readonly GraphAuthenticator _authenticator;
    private readonly GraphHttpClient _http;
    private readonly OneDriveOperations _operations;
    private readonly OneDrivePathSyntax _paths = new();

    public OneDriveProvider(GraphAuthenticator authenticator, GraphHttpClient http)
    {
        _authenticator = authenticator;
        _http = http;
        _operations = new OneDriveOperations(http);

        _authenticator.Activity += (_, activity) => Activity?.Invoke(this, activity);
        _http.Activity += (_, activity) => Activity?.Invoke(this, activity);
    }

    public ProviderId Id => ProviderId.OneDrive;

    public string DisplayName => "OneDrive";

    /// <summary>
    /// Per docs/PLAN-CLOUD-PROVIDERS.md §4.2–§4.5. <c>SupportsDelta</c> is false — P8, not this
    /// phase, even though Graph does support it. <c>RemoteHash = QuickXor</c>: only quickXorHash
    /// is trusted (see <see cref="OneDriveOperations.ToDriveItem"/>'s reasoning), unverified
    /// against a live personal-drive account until the live-verification session runs.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RemoteHash: RemoteHashAlgorithm.QuickXor,
        SupportsServerSideMove: true,
        SupportsServerSideCopy: true,
        CopyIsAsynchronous: true,
        SupportsBatchMove: false,
        SupportsDelta: false,
        RequiresRemoteViewInvalidation: false,
        MaxSingleShotUploadBytes: 4L * 1024 * 1024,
        UploadChunkSizeBytes: 10 * 320 * 1024,
        MaxRecommendedConcurrency: 4,
        CanSetRemoteModificationTime: true);

    public IDriveOperations Operations => _operations;

    public IDriveAuthenticator Auth => _authenticator;

    public IProviderPathSyntax Paths => _paths;

    /// <summary>Graph listings are served fresh by the service — nothing local to invalidate.</summary>
    public IRemoteViewInvalidator? RemoteView => null;

    /// <summary>No external binary to version — there is nothing for this to report.</summary>
    public IProviderDiagnostics? Diagnostics => null;

    public event EventHandler<ProviderActivity>? Activity;

    /// <summary>Graph's JSON responses never fail to parse the way Proton's best-effort text fallback does — nothing to warn about here.</summary>
    public event EventHandler<string>? ListingParseWarning;

    public void Dispose() => _http.Dispose();
}
