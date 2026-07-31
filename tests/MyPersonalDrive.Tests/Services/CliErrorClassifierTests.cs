using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

public class CliErrorClassifierTests
{
    [Theory]
    [InlineData("Error: login first")]
    [InlineData("You are not authenticated")]
    [InlineData("not logged in")]
    public void DetectsNotAuthenticated(string stderr)
    {
        Assert.Equal(CliErrorKind.NotAuthenticated, CliErrorClassifier.Classify(1, "", stderr));
    }

    [Theory]
    [InlineData("Path does not exist")]
    [InlineData("File not found")]
    [InlineData("no such file or directory")]
    public void DetectsNotFound(string stderr)
    {
        Assert.Equal(CliErrorKind.NotFound, CliErrorClassifier.Classify(1, "", stderr));
    }

    [Fact]
    public void DetectsAlreadyExists()
    {
        Assert.Equal(CliErrorKind.AlreadyExists, CliErrorClassifier.Classify(1, "", "A folder with that name already exists"));
    }

    [Fact]
    public void UnrecognizedMessage_IsUnknown()
    {
        Assert.Equal(CliErrorKind.Unknown, CliErrorClassifier.Classify(1, "", "Something went sideways"));
    }

    [Fact]
    public void FallsBackToStdoutWhenStderrIsEmpty()
    {
        Assert.Equal(CliErrorKind.NotFound, CliErrorClassifier.Classify(1, "the requested path was not found", ""));
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        Assert.Equal(CliErrorKind.NotAuthenticated, CliErrorClassifier.Classify(1, "", "LOGIN FIRST"));
    }
}
