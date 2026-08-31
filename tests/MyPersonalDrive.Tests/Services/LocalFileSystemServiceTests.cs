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
}
