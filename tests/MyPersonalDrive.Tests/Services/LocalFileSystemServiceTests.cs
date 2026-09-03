using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

public class LocalFileSystemServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-localfs").FullName;
    private readonly LocalFileSystemService _sut = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ListDirectory_ReturnsFilesAndFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "note.txt"), "hi");

        var items = _sut.ListDirectory(_root, includeHidden: false);

        Assert.Contains(items, i => i.Name == "sub" && i.IsFolder);
        Assert.Contains(items, i => i.Name == "note.txt" && !i.IsFolder && i.Size == 2);
    }

    [Fact]
    public void ListDirectory_HidesDotfilesByDefault_AndShowsThemWhenAsked()
    {
        File.WriteAllText(Path.Combine(_root, ".hidden"), "x");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "x");

        var hidden = _sut.ListDirectory(_root, includeHidden: false);
        Assert.DoesNotContain(hidden, i => i.Name == ".hidden");
        Assert.Contains(hidden, i => i.Name == "visible.txt");

        var all = _sut.ListDirectory(_root, includeHidden: true);
        Assert.Contains(all, i => i.Name == ".hidden");
    }

    [Fact]
    public void ListDirectory_FolderHasNoSize()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var items = _sut.ListDirectory(_root, includeHidden: false);

        Assert.Null(Assert.Single(items).Size);
    }

    [Fact]
    public void AvailableFreeBytes_ReturnsAPositiveValue()
    {
        var free = _sut.AvailableFreeBytes(_root);

        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Fact]
    public void GetHomeDirectory_ReturnsANonEmptyPath()
        => Assert.False(string.IsNullOrWhiteSpace(_sut.GetHomeDirectory()));

    [Fact]
    public void Delete_RemovesAFile()
    {
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "x");

        _sut.Delete(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Delete_RemovesAFolder_Recursively()
    {
        var folder = Path.Combine(_root, "sub");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");

        _sut.Delete(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Rename_MovesAFileWithinItsParent_AndReturnsTheNewPath()
    {
        var file = Path.Combine(_root, "old.txt");
        File.WriteAllText(file, "x");

        var newPath = _sut.Rename(file, "new.txt");

        Assert.Equal(Path.Combine(_root, "new.txt"), newPath);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void Rename_MovesAFolderWithinItsParent()
    {
        var folder = Path.Combine(_root, "old-folder");
        Directory.CreateDirectory(folder);

        var newPath = _sut.Rename(folder, "new-folder");

        Assert.Equal(Path.Combine(_root, "new-folder"), newPath);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(newPath));
    }
}
