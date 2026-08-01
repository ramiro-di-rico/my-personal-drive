using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

public class CliErrorClassifierTests
{
    [Theory]
    // The first case is the real message, captured verbatim from cli-drive@0.4.2 on stderr with
    // exit code 1 (docs/PLAN-LOCAL-SYNC.md Appendix A #10) — the rest are defensive variants.
    [InlineData("You need to login first")]
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
    public void DetectsTheCliLosingARaceOnItsOwnSqliteCache()
    {
        // Verified against the real CLI (docs/PLAN-LOCAL-SYNC.md Appendix A #11): concurrent
        // `proton-drive` processes crash this way, and it's purely transient.
        Assert.Equal(CliErrorKind.Busy, CliErrorClassifier.Classify(1, "code: \"SQLITE_BUSY\"", ""));
        Assert.Equal(CliErrorKind.Busy, CliErrorClassifier.Classify(1, "", "database is locked"));
    }

    [Fact]
    public void ClassifiesUsingStdoutWhenStderrIsJustTheCrashBanner()
    {
        // The real failure mode: the banner lands on stderr and the diagnosis on stdout. Reading
        // only stderr classified this as Unknown and produced a "===============" error message.
        const string banner = "===============================================\n";
        Assert.Equal(CliErrorKind.Busy, CliErrorClassifier.Classify(1, "{ code: 'SQLITE_BUSY', errno: 5 }", banner));
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        Assert.Equal(CliErrorKind.NotAuthenticated, CliErrorClassifier.Classify(1, "", "LOGIN FIRST"));
    }
}
