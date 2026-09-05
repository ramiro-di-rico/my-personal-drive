using MyPersonalDrive.Services.Providers.GoogleDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.GoogleDrive;

public class GoogleDriveTokenStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.GoogleDriveTokenStore").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_WithNothingSaved_ReturnsNull()
    {
        var sut = new GoogleDriveTokenStore(_tempDir);

        Assert.Null(sut.Load());
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var sut = new GoogleDriveTokenStore(_tempDir);
        var token = new StoredGoogleDriveToken
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            AccountLabel = "user@example.com",
        };

        sut.Save(token);
        var loaded = sut.Load();

        Assert.NotNull(loaded);
        Assert.Equal(token.AccessToken, loaded.AccessToken);
        Assert.Equal(token.RefreshToken, loaded.RefreshToken);
        Assert.Equal(token.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(token.AccountLabel, loaded.AccountLabel);
    }

    [Fact]
    public void Save_RestrictsPermissionsToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sut = new GoogleDriveTokenStore(_tempDir);
        sut.Save(new StoredGoogleDriveToken { AccessToken = "a", RefreshToken = "b", ExpiresAt = DateTimeOffset.UtcNow });

        var mode = File.GetUnixFileMode(Path.Combine(_tempDir, "google-drive-token.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Clear_RemovesTheFile()
    {
        var sut = new GoogleDriveTokenStore(_tempDir);
        sut.Save(new StoredGoogleDriveToken { AccessToken = "a", RefreshToken = "b", ExpiresAt = DateTimeOffset.UtcNow });

        sut.Clear();

        Assert.Null(sut.Load());
    }

    [Fact]
    public void Clear_WithNothingSaved_DoesNotThrow()
    {
        var sut = new GoogleDriveTokenStore(_tempDir);

        sut.Clear();
    }

    [Fact]
    public void Load_WithCorruptFile_DegradesToNullRatherThanThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "google-drive-token.json"), "{ not valid json");

        var sut = new GoogleDriveTokenStore(_tempDir);

        Assert.Null(sut.Load());
    }
}
