using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.GoogleDrive;

/// <summary>
/// Adapts the Drive v3 pieces (<see cref="GoogleDriveAuthenticator"/>, <see cref="GoogleDriveHttpClient"/>,
/// <see cref="GoogleDriveOperations"/>, <see cref="GoogleDrivePathSyntax"/>) to
/// <see cref="ICloudDriveProvider"/> — the Google Drive counterpart to
/// <c>Providers.OneDrive.OneDriveProvider</c>. See docs/PLAN-CLOUD-PROVIDERS.md P10.
/// </summary>
public sealed class GoogleDriveProvider : ICloudDriveProvider, IDisposable
{
    private readonly GoogleDriveAuthenticator _authenticator;
    private readonly GoogleDriveHttpClient _http;
    private readonly GoogleDriveOperations _operations;
    private readonly GoogleDrivePathSyntax _paths = new();

    public GoogleDriveProvider(GoogleDriveAuthenticator authenticator, GoogleDriveHttpClient http)
    {
        _authenticator = authenticator;
        _http = http;
        _operations = new GoogleDriveOperations(http);

        _authenticator.Activity += (_, activity) => Activity?.Invoke(this, activity);
        _http.Activity += (_, activity) => Activity?.Invoke(this, activity);
    }

    public ProviderId Id => ProviderId.GoogleDrive;

    public string DisplayName => "Google Drive";

    /// <summary>
    /// Per docs/PLAN-CLOUD-PROVIDERS.md §8.1–§8.6. <c>RemoteHash = Sha256</c>: Drive's docs show all
    /// three checksums populated together for any binary file, unlike OneDrive's real per-drive-type
    /// split, so this is a fixed choice, not a per-item fallback — unverified against a live account
    /// until the live-verification session runs. <c>SupportsDelta = false</c>: `changes.list` is
    /// explicitly deferred past this phase (§8.9). <c>CopyIsAsynchronous = false</c>: Drive's `copy`
    /// completes synchronously, unlike Graph's 202 + monitor-URL dance.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RemoteHash: RemoteHashAlgorithm.Sha256,
        SupportsServerSideMove: true,
        SupportsServerSideCopy: true,
        CopyIsAsynchronous: false,
        SupportsBatchMove: false,
        SupportsDelta: false,
        RequiresRemoteViewInvalidation: false,
        MaxSingleShotUploadBytes: 5L * 1024 * 1024,
        UploadChunkSizeBytes: 8 * 256 * 1024,
        MaxRecommendedConcurrency: 4,
        CanSetRemoteModificationTime: true,
        SupportsShareLinks: true);

    public IDriveOperations Operations => _operations;

    public IDriveAuthenticator Auth => _authenticator;

    public IProviderPathSyntax Paths => _paths;

    /// <summary>Drive's REST responses are served fresh — nothing local to invalidate.</summary>
    public IRemoteViewInvalidator? RemoteView => null;

    /// <summary>No external binary to version — there is nothing for this to report.</summary>
    public IProviderDiagnostics? Diagnostics => null;

    /// <summary>Drive's `changes.list` delta query is explicitly deferred past P10 — see docs/PLAN-CLOUD-PROVIDERS.md §8.9.</summary>
    public IDeltaSource? DeltaSource => null;

    public event EventHandler<ProviderActivity>? Activity;

    /// <summary>Drive's JSON responses never fail to parse the way Proton's best-effort text fallback does — nothing to warn about here.</summary>
    public event EventHandler<string>? ListingParseWarning;

    public void Dispose() => _http.Dispose();
}
