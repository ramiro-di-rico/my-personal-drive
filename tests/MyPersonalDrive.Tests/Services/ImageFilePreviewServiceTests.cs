using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The image viewer's loader — same download/read/cleanup shape as
/// <see cref="TextFilePreviewServiceTests"/>, minus any decoding: the bytes go to the view
/// undecoded (view models never touch Avalonia types, AGENTS.md).
/// </summary>
public class ImageFilePreviewServiceTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ImagePreview").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class DownloadingOperations(string fileName, byte[] content) : IDriveOperations
    {
        public List<string> DownloadFolders { get; } = [];

        public Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default)
        {
            DownloadFolders.Add(localFolder);
            File.WriteAllBytes(Path.Combine(localFolder, fileName), content);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashItemAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static DriveItem ImageFile(string name = "photo.jpg") =>
        new($"/my-files/{name}", name, IsFolder: false, Size: 32);

    [Fact]
    public async Task ReadsTheDownloadedBytesAndLeavesNothingBehind()
    {
        byte[] content = [0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02];
        var operations = new DownloadingOperations("photo.jpg", content);
        var service = new ImageFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(ImageFile());

        Assert.Equal(content, preview.Bytes);
        Assert.Equal(content.Length, preview.ByteCount);
        Assert.Equal("/my-files/photo.jpg", preview.Path);
        Assert.Equal("photo.jpg", preview.Name);

        var folder = Assert.Single(operations.DownloadFolders);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>The folder is ours and holds exactly one file, so an unexpected name still works.</summary>
    [Fact]
    public async Task FallsBackToWhateverLandedInTheFolder()
    {
        var operations = new DownloadingOperations("photo (1).jpg", [1, 2, 3]);
        var service = new ImageFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(ImageFile());

        Assert.Equal<byte[]>([1, 2, 3], preview.Bytes);
    }

    private sealed class EmptyDownloadOperations : IDriveOperations
    {
        public Task DownloadFileAsync(string path, string localFolder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath, UploadConflictStrategy strategy = UploadConflictStrategy.None, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TrashItemAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateShareLinkAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReportsAnEmptyDownloadInsteadOfShowingAnEmptyImage()
    {
        var service = new ImageFilePreviewService(new EmptyDownloadOperations(), _tempRoot);

        await Assert.ThrowsAnyAsync<IOException>(() => service.LoadAsync(ImageFile()));
    }

    [Fact]
    public async Task RefusesAFolder()
    {
        var service = new ImageFilePreviewService(new EmptyDownloadOperations(), _tempRoot);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.LoadAsync(new DriveItem("/my-files/photos", "photos", IsFolder: true)));
    }
}
