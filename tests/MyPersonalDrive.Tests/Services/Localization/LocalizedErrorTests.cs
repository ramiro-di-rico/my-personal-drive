using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Localization;

/// <summary>
/// PLAN-TECH-DEBT.md B6.5. An exception has to satisfy two readers at once: the CLI console and
/// the crash log want a stable English sentence that never moves with the user's language, and the
/// screen wants the user's language. It carries both.
/// </summary>
public class LocalizedErrorTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(LanguageCatalog.DefaultCode);

    private static DriveException WithDetail(LocalizedText detail) => new(
        "GET /files", exitCode: 1, stdout: string.Empty, stderr: string.Empty,
        "There is no saved OneDrive session.", DriveErrorKind.NotAuthenticated)
    {
        Detail = detail,
    };

    [Fact]
    public void TheMessageStaysEnglishWhateverTheLanguage()
    {
        var exception = WithDetail(LocalizedText.Of(StringKeys.Error.AuthNoSession, "OneDrive"));

        Localizer.Instance.SetLanguage("es");

        // What the console and the crash log see. Stable, greppable, and not the user's language.
        Assert.Equal("There is no saved OneDrive session.", exception.Message);
    }

    [Fact]
    public void TheDetailFollowsTheLanguage()
    {
        var exception = WithDetail(LocalizedText.Of(StringKeys.Error.AuthNoSession, "OneDrive"));

        Assert.Equal("There is no saved OneDrive session.", exception.DescribeForUser().Render());

        Localizer.Instance.SetLanguage("es");

        Assert.Equal("No hay una sesión de OneDrive guardada.", exception.DescribeForUser().Render());
    }

    /// <summary>
    /// The other half of §9's rule: when the message is the provider's own words rather than ours,
    /// there is no Detail and the words are shown verbatim. Paraphrasing them is how the detail
    /// that says whose problem it is gets lost.
    /// </summary>
    [Fact]
    public void WithoutADetail_TheProvidersOwnWordsAreShownVerbatim()
    {
        var exception = new DriveException(
            "GET /files", exitCode: 429, stdout: string.Empty, stderr: string.Empty,
            "Rate limit exceeded (HTTP 429). Please wait.", DriveErrorKind.RateLimited);

        Assert.True(exception.Detail.IsEmpty);
        Assert.Equal("Rate limit exceeded (HTTP 429). Please wait.", exception.DescribeForUser().Render());
    }

    [Fact]
    public void APlainExceptionFallsBackToItsMessage()
        => Assert.Equal("boom", new InvalidOperationException("boom").DescribeForUser().Render());

    [Theory]
    [InlineData(typeof(LocalizedIOException))]
    [InlineData(typeof(LocalizedFileNotFoundException))]
    [InlineData(typeof(LocalizedInvalidOperationException))]
    public void TheWrapperTypesStayCatchableAsTheirBase(Type type)
    {
        // The point of subclassing rather than introducing a new hierarchy: every existing
        // `catch (IOException)` keeps working.
        Assert.True(typeof(Exception).IsAssignableFrom(type));
        Assert.True(typeof(ILocalizedError).IsAssignableFrom(type));
    }

    [Fact]
    public void ALocalizedIOExceptionIsAnIOException()
    {
        static void Throw() => throw new LocalizedIOException(
            "no bytes", LocalizedText.Of(StringKeys.Error.CliNothingDownloaded, "a.txt"));

        var thrown = Assert.ThrowsAny<IOException>(Throw);

        Assert.Equal("The CLI reported success but downloaded nothing for 'a.txt'.", thrown.DescribeForUser().Render());
    }
}
