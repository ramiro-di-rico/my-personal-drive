using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>What the in-app PDF viewer offers itself for — the PDF counterpart to <see cref="ImagePreviewPolicyTests"/>.</summary>
public class PdfPreviewPolicyTests
{
    private static DriveItem File(string name, long? size = 1024) =>
        new($"/my-files/{name}", name, IsFolder: false, Size: size);

    [Theory]
    [InlineData("invoice.pdf")]
    [InlineData("invoice.PDF")]
    public void OffersItselfForPdfFiles(string name)
        => Assert.True(PdfPreviewPolicy.CanPreview(File(name)));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("photo.jpg")]
    [InlineData("report.docx")]
    public void StaysOutOfTheWayForNonPdfFiles(string name)
        => Assert.False(PdfPreviewPolicy.CanPreview(File(name)));

    [Fact]
    public void RefusesFolders()
        => Assert.False(PdfPreviewPolicy.CanPreview(new DriveItem("/my-files/docs", "docs", IsFolder: true)));

    [Fact]
    public void RefusesAFileBiggerThanTheLimit()
    {
        Assert.False(PdfPreviewPolicy.CanPreview(File("huge.pdf", PdfPreviewPolicy.MaxPreviewBytes + 1)));
        Assert.True(PdfPreviewPolicy.CanPreview(File("big.pdf", PdfPreviewPolicy.MaxPreviewBytes)));
    }

    [Fact]
    public void AllowsAFileWithNoReportedSize()
        => Assert.True(PdfPreviewPolicy.CanPreview(File("invoice.pdf", size: null)));
}
