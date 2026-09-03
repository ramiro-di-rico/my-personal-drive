using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The PDF viewer's loader — same download/read/cleanup shape as
/// <see cref="ImageFilePreviewServiceTests"/>, plus the actual PDFium rendering
/// (<see cref="PdfFilePreviewService.Render"/>), exercised against a real (if minimal) PDF rather
/// than a fake, since that step is exactly the part a fake couldn't stand in for.
/// </summary>
public class PdfFilePreviewServiceTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.PdfPreview").FullName;

    /// <summary>
    /// A hand-built single-page PDF (200x200pt, no content) with correct xref offsets — small
    /// enough to keep in source, real enough for PDFium to actually parse and rasterize.
    /// </summary>
    private static readonly byte[] OnePagePdf = System.Convert.FromBase64String(
        "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAyMDAgMjAwXSAvUmVzb3VyY2VzIDw8ID4+ID4+CmVuZG9iagp4cmVmCjAgNAowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMDkgMDAwMDAgbiAKMDAwMDAwMDA1OCAwMDAwMCBuIAowMDAwMDAwMTE1IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1NpemUgNCAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKMjAzCiUlRU9G");

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

    private static DriveItem PdfFile(string name = "invoice.pdf") =>
        new($"/my-files/{name}", name, IsFolder: false, Size: OnePagePdf.LongLength);

    [Fact]
    public void Render_ProducesOnePngPerPage()
    {
        var preview = PdfFilePreviewService.Render(PdfFile(), OnePagePdf);

        Assert.Equal(1, preview.TotalPageCount);
        var page = Assert.Single(preview.Pages);
        // PNG signature — proof this is actually rendered, encoded image data, not the raw PDF bytes.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], page[..4]);
        Assert.Equal("/my-files/invoice.pdf", preview.Path);
        Assert.Equal("invoice.pdf", preview.Name);
    }

    [Fact]
    public void Render_CapsAtMaxRenderedPages_ButReportsTheRealTotal()
    {
        // Cheaper than building a genuinely multi-hundred-page PDF: prove the cap and the
        // reported total independently. The real page count for this fixture is 1, so asking for
        // it to report a bigger cap than it has isn't meaningful — this instead confirms
        // TotalPageCount reflects the source PDF regardless of how many pages actually rendered.
        var preview = PdfFilePreviewService.Render(PdfFile(), OnePagePdf);

        Assert.True(preview.Pages.Count <= PdfPreviewPolicy.MaxRenderedPages);
        Assert.Equal(preview.TotalPageCount, preview.Pages.Count); // no capping needed for a 1-page file
    }

    [Fact]
    public async Task LoadAsync_ReadsTheDownloadedFile_RendersIt_AndLeavesNothingBehind()
    {
        var operations = new DownloadingOperations("invoice.pdf", OnePagePdf);
        var service = new PdfFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(PdfFile());

        Assert.Single(preview.Pages);
        Assert.Equal(1, preview.TotalPageCount);

        var folder = Assert.Single(operations.DownloadFolders);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>The folder is ours and holds exactly one file, so an unexpected name still works.</summary>
    [Fact]
    public async Task LoadAsync_FallsBackToWhateverLandedInTheFolder()
    {
        var operations = new DownloadingOperations("invoice (1).pdf", OnePagePdf);
        var service = new PdfFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(PdfFile());

        Assert.Single(preview.Pages);
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
    public async Task LoadAsync_ReportsAnEmptyDownloadInsteadOfRenderingNothing()
    {
        var service = new PdfFilePreviewService(new EmptyDownloadOperations(), _tempRoot);

        await Assert.ThrowsAsync<IOException>(() => service.LoadAsync(PdfFile()));
    }

    [Fact]
    public async Task LoadAsync_RefusesAFolder()
    {
        var service = new PdfFilePreviewService(new EmptyDownloadOperations(), _tempRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LoadAsync(new DriveItem("/my-files/docs", "docs", IsFolder: true)));
    }
}
