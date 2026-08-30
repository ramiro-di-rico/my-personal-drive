using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// What the in-app text viewer offers itself for. The two rules worth pinning down: previewing
/// costs a full CLI download, so a big file must not be offered at all; and a spreadsheet kind is
/// only text when it's delimited — ".xlsx" is a zip archive wearing a spreadsheet icon.
/// </summary>
public class TextPreviewPolicyTests
{
    private static DriveItem File(string name, long? size = 1024) =>
        new($"/my-files/{name}", name, IsFolder: false, Size: size);

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("README.md")]
    [InlineData("app.log")]
    [InlineData("Program.cs")]
    [InlineData("config.yaml")]
    [InlineData("data.csv")]
    [InlineData("data.TSV")]
    [InlineData("LICENSE")]
    public void OffersItselfForTextLikeNames(string name)
    {
        Assert.True(TextPreviewPolicy.CanPreview(File(name)));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("clip.mp4")]
    [InlineData("book.pdf")]
    [InlineData("sheet.xlsx")]
    [InlineData("backup.tar.gz")]
    [InlineData("slides.pptx")]
    public void StaysOutOfTheWayForEverythingElse(string name)
    {
        Assert.False(TextPreviewPolicy.CanPreview(File(name)));
    }

    [Fact]
    public void RefusesFolders()
    {
        Assert.False(TextPreviewPolicy.CanPreview(new DriveItem("/my-files/logs", "logs", IsFolder: true)));
    }

    [Fact]
    public void RefusesAFileBiggerThanTheLimit()
    {
        Assert.False(TextPreviewPolicy.CanPreview(File("huge.log", TextPreviewPolicy.MaxPreviewBytes + 1)));
        Assert.True(TextPreviewPolicy.CanPreview(File("big.log", TextPreviewPolicy.MaxPreviewBytes)));
    }

    /// <summary>
    /// A listing that reported no size must not disqualify the file: the reader truncates whatever
    /// it turns out to be, and refusing here would hide the viewer for a whole folder.
    /// </summary>
    [Fact]
    public void AllowsAFileWithNoReportedSize()
    {
        Assert.True(TextPreviewPolicy.CanPreview(File("notes.txt", size: null)));
    }
}
