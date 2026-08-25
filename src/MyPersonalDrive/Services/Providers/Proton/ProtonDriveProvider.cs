using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Adapts <see cref="ProtonDriveService"/> — the CLI-process boundary — to
/// <see cref="ICloudDriveProvider"/>. This is the only place that knows Proton's operations map
/// 1:1 onto the interface; everything else in the app talks to the facade.
/// See docs/PLAN-CLOUD-PROVIDERS.md P1.
/// </summary>
public sealed class ProtonDriveProvider : ICloudDriveProvider, IDriveOperations, IDriveAuthenticator, IRemoteViewInvalidator, IProviderDiagnostics
{
    private readonly ProtonDriveService _service;

    public ProtonDriveProvider(ProtonDriveService service)
    {
        _service = service;
        _service.CommandStarted += (_, args) => CommandStarted?.Invoke(this, args);
        _service.CommandOutput += (_, args) => CommandOutput?.Invoke(this, args);
        _service.CommandFinished += (_, args) => CommandFinished?.Invoke(this, args);
        _service.ListingParseWarning += (_, warning) => ListingParseWarning?.Invoke(this, warning);
    }

    public ProviderId Id => ProviderId.Proton;

    public string DisplayName => "Proton Drive";

    /// <summary>
    /// Capabilities as verified against the CLI in docs/PLAN-LOCAL-SYNC.md Appendix A.
    /// <c>SupportsDelta</c> is false because `filesystem list` has no recursive/delta mode (#4);
    /// <c>RequiresRemoteViewInvalidation</c> is true because of the stale-cache behavior in #16;
    /// <c>CanSetRemoteModificationTime</c> is false because uploads don't let the caller stamp a
    /// claimed mtime — the CLI derives it from the local file at upload time.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        RemoteHash: RemoteHashAlgorithm.Sha1,
        SupportsServerSideMove: true,
        SupportsServerSideCopy: true,
        CopyIsAsynchronous: false,
        SupportsBatchMove: true,
        SupportsDelta: false,
        RequiresRemoteViewInvalidation: true,
        MaxSingleShotUploadBytes: null,
        UploadChunkSizeBytes: null,
        MaxRecommendedConcurrency: 8,
        CanSetRemoteModificationTime: false);

    public IDriveOperations Operations => this;

    public IDriveAuthenticator Auth => this;

    public IRemoteViewInvalidator? RemoteView => this;

    public IProviderDiagnostics? Diagnostics => this;

    public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
    public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
    public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;
    public event EventHandler<string>? ListingParseWarning;

    Task<IReadOnlyList<DriveItem>> IDriveOperations.ListFolderAsync(string path, CancellationToken cancellationToken)
        => _service.LoadFolderAsync(path, cancellationToken);

    Task IDriveOperations.DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken)
        => _service.DownloadFileAsync(path, localFolder, cancellationToken);

    Task IDriveOperations.UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy, CancellationToken cancellationToken)
        => _service.UploadFilesAsync(localPaths, parentPath, strategy, cancellationToken);

    Task IDriveOperations.TrashItemAsync(string path, CancellationToken cancellationToken)
        => _service.TrashItemAsync(path, cancellationToken);

    Task IDriveOperations.RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
        => _service.RenameItemAsync(path, newName, cancellationToken);

    Task IDriveOperations.CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
        => _service.CreateFolderAsync(parentPath, name, cancellationToken);

    Task IDriveOperations.MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken)
        => _service.MoveItemsAsync(paths, targetParentPath, cancellationToken);

    Task IDriveOperations.CopyItemAsync(string sourcePath, string targetParentPath, string? newName, CancellationToken cancellationToken)
        => _service.CopyItemAsync(sourcePath, targetParentPath, newName, cancellationToken);

    Task IDriveAuthenticator.AuthenticateAsync(CancellationToken cancellationToken)
        => _service.AuthenticateAsync(cancellationToken);

    Task IDriveAuthenticator.LogoutAsync(CancellationToken cancellationToken)
        => _service.LogoutAsync(cancellationToken);

    Task IRemoteViewInvalidator.ResetRemoteCacheAsync(CancellationToken cancellationToken)
        => _service.ResetRemoteCacheAsync(cancellationToken);

    Task<string?> IProviderDiagnostics.GetVersionAsync(CancellationToken cancellationToken)
        => _service.GetCliVersionAsync(cancellationToken);
}
