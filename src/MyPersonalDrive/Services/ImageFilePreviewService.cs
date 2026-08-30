using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.Services;

/// <summary>
/// Loads the bytes of a remote image for the in-app viewer. Kept behind an interface so view-model
/// tests can hand back canned bytes without a CLI or a filesystem — mirrors
/// <see cref="ITextFilePreviewLoader"/>.
/// </summary>
public interface IImageFilePreviewLoader
{
    Task<ImageFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real loader. Same download-read-delete shape as <see cref="TextFilePreviewService"/> and for
/// the same reason: the CLI can only download, so a preview pays for one into a private temp folder
/// that gets deleted again — nothing is left under the user's own folders.
/// </summary>
public sealed class ImageFilePreviewService : IImageFilePreviewLoader
{
    private readonly IDriveOperations _operations;
    private readonly string _tempRoot;

    public ImageFilePreviewService(IDriveOperations operations, string? tempRoot = null)
    {
        _operations = operations;
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "MyPersonalDrive", "preview");
    }

    public async Task<ImageFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder)
        {
            throw new InvalidOperationException("Folders have no image to preview.");
        }

        var directory = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await _operations.DownloadFileAsync(item.Path, directory, cancellationToken);

            // Same reasoning as the text loader: the folder is ours and holds exactly one file, so
            // don't insist the CLI named it exactly what we expected.
            var downloadedPath = Path.Combine(directory, item.Name);
            if (!File.Exists(downloadedPath))
            {
                downloadedPath = Directory.EnumerateFiles(directory).FirstOrDefault()
                    ?? throw new IOException($"The CLI reported success but downloaded nothing for '{item.Name}'.");
            }

            var bytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken);
            return new ImageFilePreview(item.Path, item.Name, bytes, bytes.LongLength);
        }
        finally
        {
            // Best effort: a temp folder we couldn't delete must not turn a working preview into an
            // error. The OS reclaims it, and the next preview uses a fresh GUID either way.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
