using MyPersonalDrive.Services.Providers.GoogleDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.GoogleDrive;

/// <summary>docs/PLAN-CLOUD-PROVIDERS.md §8.2/§8.6 (G2/G6) — comparison, mappability, and the new duplicate-name axis.</summary>
public class GoogleDrivePathSyntaxTests
{
    private readonly GoogleDrivePathSyntax _sut = new();

    [Theory]
    [InlineData("/", "notes.txt", "/notes.txt")]
    [InlineData("/Documents", "notes.txt", "/Documents/notes.txt")]
    [InlineData("", "notes.txt", "/notes.txt")]
    public void Combine_JoinsWithASlash(string parent, string name, string expected)
    {
        Assert.Equal(expected, _sut.Combine(parent, name));
    }

    [Fact]
    public void IsRemoteNameMappableLocally_OnlyRejectsASlash()
    {
        Assert.False(_sut.IsRemoteNameMappableLocally("a/b"));
        Assert.True(_sut.IsRemoteNameMappableLocally("a:b*c?d \"quoted\""));
    }

    [Fact]
    public void IsLocalNameMappableRemotely_AcceptsEssentiallyAnyName()
    {
        Assert.True(_sut.IsLocalNameMappableRemotely("report.docx"));
        Assert.True(_sut.IsLocalNameMappableRemotely("a:b*c?d"));
        Assert.True(_sut.IsLocalNameMappableRemotely(" leading-space.txt"));
        Assert.True(_sut.IsLocalNameMappableRemotely("CON"));
    }

    [Fact]
    public void IsLocalNameMappableRemotely_RejectsASlashAndAnEmptyName()
    {
        Assert.False(_sut.IsLocalNameMappableRemotely("a/b"));
        Assert.False(_sut.IsLocalNameMappableRemotely(string.Empty));
    }

    [Fact]
    public void Comparison_IsCaseSensitive_UnlikeOneDrive()
    {
        Assert.Equal(StringComparison.Ordinal, _sut.Comparison);
    }

    [Fact]
    public void AllowsDuplicateNamesInSameParent_IsTrue_UnlikeProtonAndOneDrive()
    {
        Assert.True(_sut.AllowsDuplicateNamesInSameParent);
    }
}
