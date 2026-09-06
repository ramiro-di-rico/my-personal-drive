using System.Security.Cryptography;
using System.Text;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// The installer replaces the executable the whole app depends on, so the invariant under test is
/// blunt: <b>after any outcome, the file on disk is either the old working binary or the verified
/// new one.</b> The checksum-mismatch case is the important one — it is the only defence against
/// installing a corrupted or tampered download, and it cannot be verified by hand.
/// </summary>
public class CliUpdateInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-cli-update").FullName;

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

    private static string Sha512Of(string content)
        => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(content)));

    private static CliUpdateInstaller InstallerServing(string payload)
        => new((_, _) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload))));

    private static CliReleaseCandidate Release(string sha512)
        => new("0.7.0", "2026-07-31", "https://proton.me/download/drive/cli/0.7.0/linux-x64/proton-drive", sha512, "linux/x64");

    [Fact]
    public async Task AVerifiedDownload_ReplacesTheBinary()
    {
        var target = Path.Combine(_root, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");

        await InstallerServing("new binary").InstallAsync(Release(Sha512Of("new binary")), target);

        Assert.Equal("new binary", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task AChecksumMismatch_LeavesTheOldBinaryExactlyAsItWas()
    {
        var target = Path.Combine(_root, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");

        var ex = await Assert.ThrowsAsync<CliUpdateException>(
            () => InstallerServing("tampered payload").InstallAsync(Release(Sha512Of("what was promised")), target));

        Assert.Contains("checksum", ex.Message);
        Assert.Equal("old binary", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task AChecksumMismatch_LeavesNoTempFileBehind()
    {
        var target = Path.Combine(_root, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");

        await Assert.ThrowsAsync<CliUpdateException>(
            () => InstallerServing("tampered").InstallAsync(Release(Sha512Of("promised")), target));

        Assert.Equal([target], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task AFailedDownload_LeavesTheOldBinaryAndNoTempFile()
    {
        var target = Path.Combine(_root, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");
        var installer = new CliUpdateInstaller((_, _) => throw new IOException("connection reset"));

        await Assert.ThrowsAnyAsync<IOException>(() => installer.InstallAsync(Release(Sha512Of("x")), target));

        Assert.Equal("old binary", await File.ReadAllTextAsync(target));
        Assert.Equal([target], Directory.GetFileSystemEntries(_root));
    }

    /// <summary>Without the executable bit the swap would succeed and then break every CLI command.</summary>
    [Fact]
    public async Task TheInstalledBinary_IsExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "proton-drive");
        await InstallerServing("new binary").InstallAsync(Release(Sha512Of("new binary")), target);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.OtherExecute));
    }

    [Fact]
    public async Task TheChecksumComparison_IsCaseInsensitive()
    {
        var target = Path.Combine(_root, "proton-drive");

        await InstallerServing("new binary").InstallAsync(Release(Sha512Of("new binary").ToUpperInvariant()), target);

        Assert.Equal("new binary", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task InstallingWhereNothingExistsYet_Works()
    {
        var target = Path.Combine(_root, "nested", "proton-drive");

        await InstallerServing("fresh").InstallAsync(Release(Sha512Of("fresh")), target);

        Assert.Equal("fresh", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ProgressIsReported_InBytesWritten()
    {
        var target = Path.Combine(_root, "proton-drive");
        var reported = new List<long>();

        await InstallerServing("0123456789").InstallAsync(
            Release(Sha512Of("0123456789")), target, onProgress: reported.Add);

        Assert.Equal(10, reported[^1]);
    }

    [Fact]
    public async Task AnEmptyTargetPath_IsRefusedBeforeAnythingIsDownloaded()
    {
        var installer = new CliUpdateInstaller((_, _) => throw new InvalidOperationException("should not be reached"));

        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(Release("aa"), string.Empty));
    }
}
