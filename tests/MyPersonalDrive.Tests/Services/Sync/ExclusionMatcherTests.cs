using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class ExclusionMatcherTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("sub/.git", true)]
    [InlineData("sub/node_modules", true)]
    public void DefaultDirectories_AreExcluded_AtAnyDepth(string path, bool isDirectory)
    {
        var matcher = new ExclusionMatcher();

        Assert.True(matcher.IsExcluded(path, isDirectory));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    [InlineData("draft.tmp")]
    [InlineData("notes.swp")]
    [InlineData("~$budget.xlsx")]
    public void DefaultFileGlobs_AreExcluded(string fileName)
    {
        var matcher = new ExclusionMatcher();

        Assert.True(matcher.IsExcluded(fileName, isDirectory: false));
    }

    [Fact]
    public void RegularFile_IsNotExcluded()
    {
        var matcher = new ExclusionMatcher();

        Assert.False(matcher.IsExcluded("report.pdf", isDirectory: false));
    }

    [Fact]
    public void FileInsideExcludedDirectory_IsExcluded()
    {
        var matcher = new ExclusionMatcher();

        Assert.True(matcher.IsExcluded(".git/HEAD", isDirectory: false));
    }

    [Fact]
    public void ExtraGlob_WithTrailingSlash_ExcludesADirectoryByName()
    {
        var matcher = new ExclusionMatcher(["build/"]);

        Assert.True(matcher.IsExcluded("build", isDirectory: true));
        Assert.True(matcher.IsExcluded("sub/build", isDirectory: true));
    }

    [Fact]
    public void ExtraGlob_WithoutTrailingSlash_MatchesFileNamePattern()
    {
        var matcher = new ExclusionMatcher(["*.log"]);

        Assert.True(matcher.IsExcluded("app.log", isDirectory: false));
        Assert.False(matcher.IsExcluded("app.log.txt", isDirectory: false));
    }

    [Fact]
    public void GlobMatching_IsCaseInsensitive()
    {
        var matcher = new ExclusionMatcher();

        Assert.True(matcher.IsExcluded("THUMBS.DB", isDirectory: false));
    }
}
