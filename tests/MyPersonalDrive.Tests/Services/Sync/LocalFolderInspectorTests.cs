using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class LocalFolderInspectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-inspector").FullName;

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
    public void AWritableFolder_PassesAndLeavesNoProbeBehind()
    {
        Assert.Null(LocalFolderInspector.CheckWritable(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root)); // the write probe cleaned up after itself
    }

    [Fact]
    public void AMissingFolder_IsCreatedRatherThanRejected()
    {
        // The executor would create it on the first run anyway, so refusing here would be arbitrary.
        var target = Path.Combine(_root, "not-yet", "nested");

        Assert.Null(LocalFolderInspector.CheckWritable(target));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void AFileWhereAFolderShouldBe_IsRejectedClearly()
    {
        var asFile = Path.Combine(_root, "actually-a-file.txt");
        File.WriteAllText(asFile, "not a folder");

        Assert.Contains("es un archivo, no una carpeta", LocalFolderInspector.CheckWritable(asFile));
    }

    [PosixFact]
    public void AReadOnlyFolder_IsRejected()
    {
        // Checked by probing rather than inspecting permissions: ACLs, mount options and read-only
        // filesystems all give the same practical answer and only a write attempt covers them all.
        var readOnly = Path.Combine(_root, "read-only");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.Contains("No se puede escribir en", LocalFolderInspector.CheckWritable(readOnly));
        }
        finally
        {
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void CountingStopsAtTheCap_SoAHugeTreeIsNeverWalked()
    {
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");
        }

        Assert.Equal(5, LocalFolderInspector.CountEntriesUpTo(_root, 5));
        Assert.Equal(10, LocalFolderInspector.CountEntriesUpTo(_root, 100));
    }

    [Fact]
    public void CountingIncludesNestedEntries()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "deep.txt"), "x");

        Assert.Equal(2, LocalFolderInspector.CountEntriesUpTo(_root, 100)); // the folder and the file
    }

    [Fact]
    public void CountingAMissingFolder_IsZeroNotAnError()
        => Assert.Equal(0, LocalFolderInspector.CountEntriesUpTo(Path.Combine(_root, "nope"), 100));

    [Fact]
    public void NoDownload_MeansNoSpaceWarning()
        => Assert.Null(LocalFolderInspector.CheckFreeSpace(_root, bytesToDownload: 0));

    [Fact]
    public void AModestDownload_FitsAndIsNotWarnedAbout()
        => Assert.Null(LocalFolderInspector.CheckFreeSpace(_root, bytesToDownload: 1024));

    [Fact]
    public void ADownloadLargerThanTheDisk_IsWarnedAboutWithBothNumbers()
    {
        var warning = LocalFolderInspector.CheckFreeSpace(_root, bytesToDownload: long.MaxValue / 2);

        Assert.NotNull(warning);
        Assert.Contains("Esto descargaría", warning);
        Assert.Contains("libres en ese disco", warning);
    }

    [Fact]
    public void FreeSpaceOnAnUnknownPath_ProducesNoWarningRatherThanAFalseOne()
    {
        // Better to say nothing than to invent a shortage from a path we couldn't measure.
        Assert.Null(LocalFolderInspector.CheckFreeSpace("\0invalid", bytesToDownload: 1_000_000));
    }
}
