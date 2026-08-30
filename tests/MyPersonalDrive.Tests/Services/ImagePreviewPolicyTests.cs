using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// What the in-app image viewer offers itself for. Narrower than the text policy on purpose: only
/// formats <c>Avalonia.Media.Imaging.Bitmap</c> (SkiaSharp) actually decodes, which excludes RAW
/// camera formats and .psd despite those being <see cref="FileKind.Image"/> too, and .svg, which
/// is vector data rather than something a bitmap viewer can render.
/// </summary>
public class ImagePreviewPolicyTests
{
    private static DriveItem File(string name, long? size = 1024) =>
        new($"/my-files/{name}", name, IsFolder: false, Size: size);

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.JPEG")]
    [InlineData("icon.png")]
    [InlineData("anim.gif")]
    [InlineData("scan.bmp")]
    [InlineData("modern.webp")]
    [InlineData("favicon.ico")]
    public void OffersItselfForDecodableFormats(string name)
    {
        Assert.True(ImagePreviewPolicy.CanPreview(File(name)));
    }

    [Theory]
    [InlineData("shot.cr2")]
    [InlineData("shot.nef")]
    [InlineData("shot.dng")]
    [InlineData("shot.raw")]
    [InlineData("mockup.psd")]
    [InlineData("logo.svg")]
    [InlineData("clip.mp4")]
    [InlineData("notes.txt")]
    public void StaysOutOfTheWayForFormatsItCannotDecode(string name)
    {
        Assert.False(ImagePreviewPolicy.CanPreview(File(name)));
    }

    [Fact]
    public void RefusesFolders()
    {
        Assert.False(ImagePreviewPolicy.CanPreview(new DriveItem("/my-files/photos", "photos", IsFolder: true)));
    }

    [Fact]
    public void RefusesAFileBiggerThanTheLimit()
    {
        Assert.False(ImagePreviewPolicy.CanPreview(File("huge.png", ImagePreviewPolicy.MaxPreviewBytes + 1)));
        Assert.True(ImagePreviewPolicy.CanPreview(File("big.png", ImagePreviewPolicy.MaxPreviewBytes)));
    }

    [Fact]
    public void AllowsAFileWithNoReportedSize()
    {
        Assert.True(ImagePreviewPolicy.CanPreview(File("photo.jpg", size: null)));
    }
}
