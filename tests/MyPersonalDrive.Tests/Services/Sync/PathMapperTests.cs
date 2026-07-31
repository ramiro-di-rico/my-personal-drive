using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class PathMapperTests
{
    [Fact]
    public void ToRemoteAbsolute_CombinesRootAndRelative()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal("/my-files/Docs/sub/file.txt", mapper.ToRemoteAbsolute("sub/file.txt"));
    }

    [Fact]
    public void ToRemoteAbsolute_EmptyRelativePath_IsTheRootItself()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal("/my-files/Docs", mapper.ToRemoteAbsolute(string.Empty));
    }

    [Fact]
    public void ToLocalAbsolute_ConvertsForwardSlashesToOsSeparator()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal(Path.Combine("/home/user/Docs", "sub", "file.txt"), mapper.ToLocalAbsolute("sub/file.txt"));
    }

    [Fact]
    public void ToRelativeFromRemote_StripsTheRoot()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal("sub/file.txt", mapper.ToRelativeFromRemote("/my-files/Docs/sub/file.txt"));
    }

    [Fact]
    public void ToRelativeFromRemote_ExactRoot_IsEmptyString()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal(string.Empty, mapper.ToRelativeFromRemote("/my-files/Docs"));
    }

    [Fact]
    public void ToRelativeFromRemote_OutsideRoot_Throws()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Throws<ArgumentException>(() => mapper.ToRelativeFromRemote("/my-files/OtherFolder/file.txt"));
    }

    [Fact]
    public void ToRelativeFromRemote_SiblingWithSharedPrefix_DoesNotFalsePositiveMatch()
    {
        // "/my-files/Docs2" must not be treated as inside "/my-files/Docs".
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Throws<ArgumentException>(() => mapper.ToRelativeFromRemote("/my-files/Docs2/file.txt"));
    }

    [Fact]
    public void ToRelativeFromLocal_StripsTheRoot()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        var localPath = Path.Combine("/home/user/Docs", "sub", "file.txt");
        Assert.Equal("sub/file.txt", mapper.ToRelativeFromLocal(localPath));
    }

    [Fact]
    public void ToRelativeFromLocal_ExactRoot_IsEmptyString()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal(string.Empty, mapper.ToRelativeFromLocal("/home/user/Docs"));
    }

    [Fact]
    public void ToRelativeFromLocal_OutsideRoot_Throws()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Throws<ArgumentException>(() => mapper.ToRelativeFromLocal("/home/user/OtherFolder/file.txt"));
    }

    [Fact]
    public void RoundTrip_RemoteToRelativeToRemote_IsStable()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");
        const string remotePath = "/my-files/Docs/a/b/c.txt";

        var relative = mapper.ToRelativeFromRemote(remotePath);
        Assert.Equal(remotePath, mapper.ToRemoteAbsolute(relative));
    }

    [Fact]
    public void RoundTrip_LocalToRelativeToLocal_IsStable()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");
        var localPath = Path.Combine("/home/user/Docs", "a", "b", "c.txt");

        var relative = mapper.ToRelativeFromLocal(localPath);
        Assert.Equal(localPath, mapper.ToLocalAbsolute(relative));
    }

    [Fact]
    public void UnicodeNames_RoundTrip()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");
        const string relative = "café/résumé 日本語.txt";

        var remote = mapper.ToRemoteAbsolute(relative);
        Assert.Equal(relative, mapper.ToRelativeFromRemote(remote));

        var local = mapper.ToLocalAbsolute(relative);
        Assert.Equal(relative, mapper.ToRelativeFromLocal(local));
    }

    [Fact]
    public void CaseSensitivity_IsPreserved_NotNormalized()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        Assert.Equal("/my-files/Docs/CaseSensitive.TXT", mapper.ToRemoteAbsolute("CaseSensitive.TXT"));
        Assert.Equal("CaseSensitive.TXT", mapper.ToRelativeFromRemote("/my-files/Docs/CaseSensitive.TXT"));
    }

    [Fact]
    public void RemoteRoot_TrailingSlash_IsNormalized()
    {
        var mapper = new PathMapper("/my-files/Docs/", "/home/user/Docs");

        Assert.Equal("/my-files/Docs/file.txt", mapper.ToRemoteAbsolute("file.txt"));
    }

    [Fact]
    public void LocalRoot_TrailingSlash_IsNormalized()
    {
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs/");

        Assert.Equal(Path.Combine("/home/user/Docs", "file.txt"), mapper.ToLocalAbsolute("file.txt"));
    }
}
