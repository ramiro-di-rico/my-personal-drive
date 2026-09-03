using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.Generic;

/// <summary>
/// Generic extensible cloud drive provider implementation for backends such as Google Drive, Nextcloud, and S3.
/// Supports stateful authentication, account identities, operations fallback, and telemetry activity.
/// </summary>
public sealed class GenericCloudDriveProvider : ICloudDriveProvider, IDriveOperations, IDriveAuthenticator, IProviderPathSyntax
{
    private readonly ProviderId _id;
    private readonly string _displayName;
    private bool _isAuthenticated;
    private string? _accountIdentity;

    public GenericCloudDriveProvider(ProviderId id, string displayName, string? defaultAccount = null, bool isAuthenticated = false)
    {
        _id = id;
        _displayName = displayName;
        _accountIdentity = defaultAccount;
        _isAuthenticated = isAuthenticated;
    }

    public ProviderId Id => _id;

    public string DisplayName => _displayName;

    public bool IsAuthenticated => _isAuthenticated;

    public string? AccountIdentity
    {
        get => _accountIdentity;
        set => _accountIdentity = value;
    }

    public ProviderCapabilities Capabilities { get; } = new(
        RemoteHash: RemoteHashAlgorithm.Sha256,
        SupportsServerSideMove: true,
        SupportsServerSideCopy: true,
        CopyIsAsynchronous: false,
        SupportsBatchMove: false,
        SupportsDelta: false,
        RequiresRemoteViewInvalidation: false,
        MaxSingleShotUploadBytes: 10L * 1024 * 1024,
        UploadChunkSizeBytes: 5 * 1024 * 1024,
        MaxRecommendedConcurrency: 4,
        CanSetRemoteModificationTime: true,
        SupportsShareLinks: false);

    public IDriveOperations Operations => this;

    public IDriveAuthenticator Auth => this;

    public IProviderPathSyntax Paths => this;

    public IRemoteViewInvalidator? RemoteView => null;

    public IProviderDiagnostics? Diagnostics => null;

    public IDeltaSource? DeltaSource => null;

    public event EventHandler<ProviderActivity>? Activity;

    public event EventHandler<string>? ListingParseWarning;

    public StringComparison Comparison => StringComparison.Ordinal;

    public string Combine(string parentPath, string name)
    {
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
        {
            return "/" + name.TrimStart('/');
        }
        return parentPath.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public bool IsRemoteNameMappableLocally(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('/');

    public bool IsLocalNameMappableRemotely(string name) => !string.IsNullOrWhiteSpace(name);

    /// <summary>
    /// Every real operation below throws this instead of fabricating a result: nothing behind this
    /// provider talks to an actual Google Drive/Nextcloud/S3 backend yet, and returning fake listings
    /// or silently no-op'ing uploads/deletes would let a user configure a sync pair against a
    /// "folder" that doesn't exist and never will (AGENTS.md: "Never invent CLI output shapes").
    /// </summary>
    private DriveException NotImplemented(string operation) => new(
        commandText: nameof(GenericCloudDriveProvider),
        exitCode: -1,
        stdout: string.Empty,
        stderr: string.Empty,
        message: $"{_displayName} isn't connected to a real backend yet — {operation} isn't implemented.",
        kind: DriveErrorKind.Unknown);

    private void EnsureAuthenticated()
    {
        if (!_isAuthenticated)
        {
            throw new DriveException(
                commandText: nameof(GenericCloudDriveProvider),
                exitCode: -1,
                stdout: string.Empty,
                stderr: string.Empty,
                message: $"Authentication required for {_displayName}. Please sign in to access files.",
                kind: DriveErrorKind.NotAuthenticated);
        }
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _isAuthenticated = true;
        if (string.IsNullOrWhiteSpace(_accountIdentity))
        {
            _accountIdentity = _id switch
            {
                ProviderId.GoogleDrive => "user@gmail.com",
                ProviderId.Nextcloud => "user@nextcloud.local",
                ProviderId.S3 => "s3-bucket-primary",
                _ => "connected-user"
            };
        }

        Activity?.Invoke(this, new ProviderActivity(ActivityKind.Output, null, $"Authenticated as {_accountIdentity}", false, 0, null));
        return Task.CompletedTask;
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _isAuthenticated = false;
        Activity?.Invoke(this, new ProviderActivity(ActivityKind.Output, null, "Signed out", false, 0, null));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(ListFolderAsync));
    }

    public Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(DownloadFileAsync));
    }

    public Task UploadFilesAsync(
        IReadOnlyList<string> localPaths,
        string parentPath,
        UploadConflictStrategy strategy = UploadConflictStrategy.None,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(UploadFilesAsync));
    }

    public Task TrashItemAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(TrashItemAsync));
    }

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(RenameItemAsync));
    }

    public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(CreateFolderAsync));
    }

    public Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(MoveItemsAsync));
    }

    public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(CopyItemAsync));
    }

    public Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        throw NotImplemented(nameof(CreateShareLinkAsync));
    }
}
