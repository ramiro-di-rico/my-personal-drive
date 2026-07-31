using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class LocalScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-localscanner-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteFile(string relativePath, string content, DateTimeOffset? modifiedAt = null)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        if (modifiedAt is { } mt)
        {
            File.SetLastWriteTimeUtc(fullPath, mt.UtcDateTime);
        }
    }

    private void CreateDirectory(string relativePath)
        => Directory.CreateDirectory(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task NonExistentRoot_ReturnsEmpty()
    {
        var scanner = new LocalScanner();

        var result = await scanner.ScanAsync(Path.Combine(_root, "does-not-exist"), new ExclusionMatcher());

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindsFilesAndFolders_Recursively()
    {
        WriteFile("a.txt", "hello", DateTimeOffset.UtcNow.AddMinutes(-5));
        WriteFile("sub/b.txt", "world", DateTimeOffset.UtcNow.AddMinutes(-5));

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.True(result.ContainsKey("a.txt"));
        Assert.False(result["a.txt"].IsFolder);
        Assert.True(result.ContainsKey("sub"));
        Assert.True(result["sub"].IsFolder);
        Assert.True(result.ContainsKey("sub/b.txt"));
    }

    [Fact]
    public async Task FileSizeAndModifiedAt_AreReported()
    {
        var modifiedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        WriteFile("a.txt", "12345", modifiedAt);

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.Equal(5, result["a.txt"].Size);
        Assert.Equal(modifiedAt, result["a.txt"].ModifiedAt);
    }

    [Fact]
    public async Task ContentHash_IsAlwaysNull_ScanningIsStatOnly()
    {
        WriteFile("a.txt", "hello", DateTimeOffset.UtcNow.AddMinutes(-5));

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.Null(result["a.txt"].ContentHash);
    }

    [Fact]
    public async Task RecentlyModifiedFile_IsExcludedFromThisScan()
    {
        WriteFile("just-written.txt", "hello"); // mtime = now

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.False(result.ContainsKey("just-written.txt"));
    }

    [Fact]
    public async Task ExcludedDirectory_IsSkippedEntirely_IncludingItsContents()
    {
        WriteFile(".git/HEAD", "ref: refs/heads/main", DateTimeOffset.UtcNow.AddMinutes(-5));
        WriteFile("real.txt", "hello", DateTimeOffset.UtcNow.AddMinutes(-5));

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.DoesNotContain(result.Keys, k => k.StartsWith(".git"));
        Assert.True(result.ContainsKey("real.txt"));
    }

    [Fact]
    public async Task EmptyDirectory_IsReportedAsAFolder()
    {
        CreateDirectory("EmptyFolder");

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.True(result["EmptyFolder"].IsFolder);
    }

    [Fact]
    public async Task RelativePaths_UseForwardSlashes()
    {
        WriteFile("a/b/c.txt", "x", DateTimeOffset.UtcNow.AddMinutes(-5));

        var scanner = new LocalScanner();
        var result = await scanner.ScanAsync(_root, new ExclusionMatcher());

        Assert.Contains("a/b/c.txt", result.Keys);
        Assert.DoesNotContain(result.Keys, k => k.Contains('\\'));
    }
}
