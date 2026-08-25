using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md V3/M1. The interesting cases are the ones that would otherwise be
/// wrong in a way nobody notices: dotfiles (a leading dot names the file, it doesn't type it),
/// compound extensions, dates mistaken for extensions, and casing done culture-sensitively.
/// </summary>
public class FileKindClassifierTests
{
    [Fact]
    public void AFolder_IsAFolder_WhateverItsNameLooksLike()
        => Assert.Equal(FileKind.Folder, FileKindClassifier.Classify("archive.zip", isFolder: true));

    [Theory]
    [InlineData("photo.jpg", FileKind.Image)]
    [InlineData("clip.webm", FileKind.Video)]
    [InlineData("song.flac", FileKind.Audio)]
    [InlineData("10825139_1.pdf", FileKind.Pdf)]
    [InlineData("budget.xlsx", FileKind.Spreadsheet)]
    [InlineData("deck.pptx", FileKind.Presentation)]
    [InlineData("notes.md", FileKind.Text)]
    [InlineData("Program.cs", FileKind.Code)]
    [InlineData("backup.zip", FileKind.Archive)]
    public void KnownExtensions_MapToTheirKind(string name, FileKind expected)
        => Assert.Equal(expected, FileKindClassifier.Classify(name, isFolder: false));

    [Theory]
    [InlineData("photo.JPG")]
    [InlineData("photo.Jpg")]
    [InlineData("PHOTO.JPEG")]
    public void ExtensionMatching_IsCaseInsensitive(string name)
        => Assert.Equal(FileKind.Image, FileKindClassifier.Classify(name, isFolder: false));

    [Theory]
    [InlineData("release.tar.gz")]
    [InlineData("release.tar.zst")]
    [InlineData("release.TAR.XZ")]
    public void CompoundArchiveExtensions_AreArchives(string name)
        => Assert.Equal(FileKind.Archive, FileKindClassifier.Classify(name, isFolder: false));

    [Fact]
    public void AnUnknownCompoundExtension_FallsBackToItsLastSegment()
        => Assert.Equal(FileKind.Archive, FileKindClassifier.Classify("release.tar.wat", isFolder: false));

    [Fact]
    public void ADateInTheName_IsNotMistakenForAnExtension()
        => Assert.Equal(FileKind.Archive, FileKindClassifier.Classify("backup.2026-01-02.tar", isFolder: false));

    [Theory]
    [InlineData("Hyperlinks issue")]
    [InlineData("README")]
    [InlineData("trailing.")]
    public void ANameWithNoExtension_IsOther(string name)
        => Assert.Equal(FileKind.Other, FileKindClassifier.Classify(name, isFolder: false));

    [Fact]
    public void ADotfile_IsNotClassifiedByItsLeadingDot()
        => Assert.Equal(FileKind.Other, FileKindClassifier.Classify(".bashrc", isFolder: false));

    [Fact]
    public void ADotfileWithARealExtension_StillUsesIt()
        => Assert.Equal(FileKind.Code, FileKindClassifier.Classify(".eslintrc.json", isFolder: false));

    [Fact]
    public void AnUnknownExtension_IsOther()
        => Assert.Equal(FileKind.Other, FileKindClassifier.Classify("mystery.qqq", isFolder: false));

    [Fact]
    public void EveryKind_HasALabel()
    {
        foreach (var kind in Enum.GetValues<FileKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FileKindClassifier.DisplayName(kind)));
        }
    }
}
