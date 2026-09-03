using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers;

/// <summary>
/// The provider-neutral catalog of remote file operations — what
/// <c>Providers.Proton.ProtonDriveService</c> used to expose directly to every consumer.
/// One method per operation, same argument order as before, so lifting a caller onto this
/// interface is a type change, not a redesign. See docs/PLAN-CLOUD-PROVIDERS.md §2.2.
/// </summary>
public interface IDriveOperations
{
    Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default);

    Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default);

    Task UploadFilesAsync(
        IReadOnlyList<string> localPaths,
        string parentPath,
        UploadConflictStrategy strategy = UploadConflictStrategy.None,
        CancellationToken cancellationToken = default);

    Task TrashItemAsync(string path, CancellationToken cancellationToken = default);

    Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default);

    Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default);

    Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default);

    Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a shareable link for the item, when the provider's
    /// <see cref="ProviderCapabilities.SupportsShareLinks"/> is true. Callers must gate on that
    /// capability rather than on catching a failure here — a provider that doesn't support this
    /// (Proton's CLI has no such command) throws <see cref="DriveException"/> unconditionally, the
    /// same way <c>Providers.Generic.GenericCloudDriveProvider</c> reports every unimplemented
    /// operation.
    /// </summary>
    Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default);
}

public static class DriveOperationsExtensions
{
    /// <summary>Single-item convenience over <see cref="IDriveOperations.MoveItemsAsync"/>.</summary>
    public static Task MoveItemAsync(this IDriveOperations operations, string path, string targetParentPath, CancellationToken cancellationToken = default)
        => operations.MoveItemsAsync([path], targetParentPath, cancellationToken);
}
