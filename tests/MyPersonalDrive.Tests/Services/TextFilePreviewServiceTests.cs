using System.Text;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The viewer's loader. Two halves worth covering separately: the download dance (the temp folder
/// must be gone afterward — a preview is not a download, and must not leave a copy behind) and the
/// bounded read (truncation, encoding, and the binary sniff that catches whatever the name-based
/// policy let through).
/// </summary>
public class TextFilePreviewServiceTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Preview").FullName;

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

    /// <summary>Stands in for the CLI: writes the canned bytes into whatever folder it's handed.</summary>
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

    private static DriveItem TextFile(string name = "notes.txt") =>
        new($"/my-files/{name}", name, IsFolder: false, Size: 32);

    [Fact]
    public async Task ReadsTheDownloadedFileAndLeavesNothingBehind()
    {
        var operations = new DownloadingOperations("notes.txt", Encoding.UTF8.GetBytes("hola\nmundo\n"));
        var service = new TextFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(TextFile());

        Assert.Equal("hola\nmundo\n", preview.Text);
        Assert.Equal(2, preview.LineCount);
        Assert.Equal("UTF-8", preview.EncodingName);
        Assert.False(preview.IsTruncated);
        Assert.False(preview.IsBinary);
        Assert.Equal("/my-files/notes.txt", preview.Path);

        var folder = Assert.Single(operations.DownloadFolders);
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>
    /// The CLI is trusted to write *a* file, not to write the exact name: the folder is ours and
    /// holds one file, so the preview must still work if the name differs.
    /// </summary>
    [Fact]
    public async Task FallsBackToWhateverLandedInTheFolder()
    {
        var operations = new DownloadingOperations("notes (1).txt", Encoding.UTF8.GetBytes("contenido"));
        var service = new TextFilePreviewService(operations, _tempRoot);

        var preview = await service.LoadAsync(TextFile());

        Assert.Equal("contenido", preview.Text);
    }

    [Fact]
    public async Task ReportsAnEmptyDownloadInsteadOfShowingAnEmptyFile()
    {
        var operations = new EmptyDownloadOperations();
        var service = new TextFilePreviewService(operations, _tempRoot);

        await Assert.ThrowsAsync<IOException>(() => service.LoadAsync(TextFile()));
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
    public async Task RefusesAFolder()
    {
        var service = new TextFilePreviewService(new EmptyDownloadOperations(), _tempRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LoadAsync(new DriveItem("/my-files/logs", "logs", IsFolder: true)));
    }

    [Fact]
    public void FlagsBinaryContentInsteadOfRenderingIt()
    {
        var path = WriteLocal("blob.bin", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        var preview = TextFilePreviewService.Read(path, "/my-files/blob.bin", "blob.bin");

        Assert.True(preview.IsBinary);
        Assert.Equal(string.Empty, preview.Text);
        Assert.Equal(6, preview.ByteCount);
    }

    [Fact]
    public void FallsBackToLatin1ForBytesThatArentUtf8()
    {
        // 0xF1 alone is "ñ" in Latin-1 and an invalid lead byte in UTF-8.
        var path = WriteLocal("legacy.txt", [(byte)'a', 0xF1, (byte)'o']);

        var preview = TextFilePreviewService.Read(path, "/my-files/legacy.txt", "legacy.txt");

        Assert.Equal("año", preview.Text);
        Assert.Equal("Latin-1", preview.EncodingName);
    }

    [Fact]
    public void TruncatesByLinesAndSaysSo()
    {
        var content = string.Join('\n', Enumerable.Range(0, TextPreviewPolicy.MaxPreviewLines + 50).Select(i => $"línea {i}"));
        var path = WriteLocal("long.log", Encoding.UTF8.GetBytes(content));

        var preview = TextFilePreviewService.Read(path, "/my-files/long.log", "long.log");

        Assert.True(preview.IsTruncated);
        Assert.Equal(TextPreviewPolicy.MaxPreviewLines, preview.LineCount);
        Assert.StartsWith("línea 0\n", preview.Text);
        Assert.DoesNotContain($"línea {TextPreviewPolicy.MaxPreviewLines}\n", preview.Text);
    }

    /// <summary>
    /// The byte cut must not land inside a multi-byte character, or strict UTF-8 decoding fails and
    /// the whole file comes back as Latin-1 mojibake.
    /// </summary>
    [Fact]
    public void TruncatesByBytesWithoutBreakingTheEncoding()
    {
        // "ñ" is two bytes; filling with it guarantees the cut lands mid-character.
        var content = Encoding.UTF8.GetBytes(new string('ñ', (int)TextPreviewPolicy.MaxPreviewBytes));
        var path = WriteLocal("huge.txt", content);

        var preview = TextFilePreviewService.Read(path, "/my-files/huge.txt", "huge.txt");

        Assert.True(preview.IsTruncated);
        Assert.Equal("UTF-8", preview.EncodingName);
        // Bytes *read*, not the file's size: the reader stops one byte past its limit precisely so
        // it can tell there was more, and never reads the rest.
        Assert.Equal(TextPreviewPolicy.MaxPreviewBytes + 1, preview.ByteCount);
        Assert.All(preview.Text, c => Assert.Equal('ñ', c));
        Assert.Equal(TextPreviewPolicy.MaxPreviewBytes / 2, preview.Text.Length);
    }

    /// <summary>
    /// The companion case to the truncation test above: when the byte cut happens to land exactly
    /// on a character boundary, nothing should be trimmed away.
    /// </summary>
    [Fact]
    public void KeepsTheLastCharacterWhenTheCutLandsExactlyOnABoundary()
    {
        // "ab" (1 byte each) followed by "ñ" (2 bytes) repeated so the file is longer than the
        // limit, but MaxPreviewBytes itself lands right after a complete "ñ".
        var unit = "ñ";
        var repeats = (int)(TextPreviewPolicy.MaxPreviewBytes / 2) + 10;
        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(unit, repeats)));
        var path = WriteLocal("boundary.txt", content);

        var preview = TextFilePreviewService.Read(path, "/my-files/boundary.txt", "boundary.txt");

        Assert.True(preview.IsTruncated);
        Assert.Equal(TextPreviewPolicy.MaxPreviewBytes / 2, preview.Text.Length);
        Assert.All(preview.Text, c => Assert.Equal('ñ', c));
    }

    [Fact]
    public void AnEmptyFileIsZeroLinesAndNotBinary()
    {
        var path = WriteLocal("empty.txt", []);

        var preview = TextFilePreviewService.Read(path, "/my-files/empty.txt", "empty.txt");

        Assert.False(preview.IsBinary);
        Assert.Equal(0, preview.LineCount);
        Assert.Equal(string.Empty, preview.Text);
    }

    private string WriteLocal(string name, byte[] content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
