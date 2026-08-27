using MyPersonalDrive.Services.Providers.OneDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>docs/PLAN-CLOUD-PROVIDERS.md §4.6 (O6) — both mappability directions, plus the path-building and comparison rules.</summary>
public class OneDrivePathSyntaxTests
{
    private readonly OneDrivePathSyntax _sut = new();

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
        Assert.True(_sut.IsRemoteNameMappableLocally("a:b*c?d"));
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("2026 plan.txt")]
    [InlineData("under_score.txt")]
    public void IsLocalNameMappableRemotely_AcceptsOrdinaryNames(string name)
    {
        Assert.True(_sut.IsLocalNameMappableRemotely(name));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a:b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a?b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    public void IsLocalNameMappableRemotely_RejectsReservedCharacters(string name)
    {
        Assert.False(_sut.IsLocalNameMappableRemotely(name));
    }

    [Theory]
    [InlineData(" leading-space.txt")]
    [InlineData("trailing-space.txt ")]
    [InlineData("trailing-dot.")]
    public void IsLocalNameMappableRemotely_RejectsLeadingTrailingSpaceAndTrailingDot(string name)
    {
        Assert.False(_sut.IsLocalNameMappableRemotely(name));
    }

    [Theory]
    [InlineData(".lock")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("desktop.ini")]
    public void IsLocalNameMappableRemotely_RejectsReservedNames(string name)
    {
        Assert.False(_sut.IsLocalNameMappableRemotely(name));
    }

    [Fact]
    public void IsLocalNameMappableRemotely_RejectsTemporaryOfficeLockFiles()
    {
        Assert.False(_sut.IsLocalNameMappableRemotely("~$report.docx"));
    }

    [Fact]
    public void IsLocalNameMappableRemotely_RejectsEmptyName()
    {
        Assert.False(_sut.IsLocalNameMappableRemotely(string.Empty));
    }

    [Fact]
    public void Comparison_IsCaseInsensitive_UnlikeProton()
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, _sut.Comparison);
    }
}
