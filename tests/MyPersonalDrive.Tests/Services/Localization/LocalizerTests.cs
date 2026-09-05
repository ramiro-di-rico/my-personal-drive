using Xunit;
using System.Globalization;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Tests.Services.Localization;

/// <summary>
/// Behaviour of the string table itself. Every test builds its own <see cref="Localizer"/> through
/// the internal constructor rather than touching <c>Localizer.Instance</c>: switching the singleton
/// also switches the process-wide default culture, which would leak into every other test.
/// </summary>
public class LocalizerTests
{
    [Fact]
    public void DefaultsToEnglish()
    {
        var localizer = new Localizer();

        Assert.Equal("en", localizer.Current.Code);
        Assert.Equal("General preferences", localizer[StringKeys.Settings.GeneralTitle]);
    }

    [Fact]
    public void ResolvesTheRequestedLanguage()
    {
        var localizer = new Localizer("es");

        Assert.Equal("es", localizer.Current.Code);
        Assert.Equal("Preferencias generales", localizer[StringKeys.Settings.GeneralTitle]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kl")]
    [InlineData("es-AR-x-nonsense")]
    public void AnUnknownLanguageCodeFallsBackToEnglishRatherThanThrowing(string? code)
    {
        Assert.Equal("en", new Localizer(code).Current.Code);
        Assert.Equal("en", LanguageCatalog.ResolveOrDefault(code).Code);
    }

    [Fact]
    public void LanguageCodesAreMatchedCaseInsensitively()
        => Assert.Equal("es", LanguageCatalog.ResolveOrDefault("ES").Code);

    [Fact]
    public void AMissingKeyDoesNotThrowAndDoesNotRenderBlank()
    {
        var localizer = new Localizer("es");

        var rendered = localizer["no.such.key.exists"];

        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.Contains("no.such.key.exists", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyKeyRendersEmptyRatherThanAMarker()
        => Assert.Equal(string.Empty, new Localizer()[string.Empty]);

    [Fact]
    public void FormatSubstitutesPositionalArguments()
    {
        var localizer = new Localizer();

        Assert.Equal("Sign in to Proton Drive", localizer.F(StringKeys.Settings.SignInTooltip, "Proton Drive"));
    }

    [Theory]
    [InlineData(0, "0 active operations")]
    [InlineData(1, "1 active operation")]
    [InlineData(2, "2 active operations")]
    public void PluralSelectsTheCategoryForTheCount(int count, string expected)
        => Assert.Equal(expected, new Localizer().Plural(StringKeys.Console.ActiveOperations, count));

    [Theory]
    [InlineData(0, "0 operaciones activas")]
    [InlineData(1, "1 operación activa")]
    [InlineData(2, "2 operaciones activas")]
    public void PluralFollowsTheLanguage(int count, string expected)
        => Assert.Equal(expected, new Localizer("es").Plural(StringKeys.Console.ActiveOperations, count));

    /// <summary>
    /// The markup used to carry "{0} operación(es) activa(s)" in a StringFormat — a Spanish-specific
    /// hack no other language can reproduce. Whatever a locale is missing, Plural must still render
    /// a sentence rather than a marker.
    /// </summary>
    [Fact]
    public void PluralFallsBackToOtherWhenTheCategoryKeyIsMissing()
    {
        var localizer = new Localizer();

        var rendered = localizer.Plural("console.activeoperations", 7);

        Assert.Equal("7 active operations", rendered);
    }

    [Fact]
    public void SetLanguageSwitchesTheStringsAndTheCulture()
    {
        var localizer = new Localizer();

        localizer.SetLanguage("es");

        Assert.Equal("es", localizer.Current.Code);
        Assert.Equal("Conexión", localizer[StringKeys.Settings.ConnectionTitle]);
        Assert.Equal("es", localizer.Culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void SetLanguageRaisesTheIndexerChangeThenLanguageChanged()
    {
        var localizer = new Localizer();
        var order = new List<string>();
        localizer.PropertyChanged += (_, e) => order.Add("property:" + e.PropertyName);
        localizer.LanguageChanged += (_, _) => order.Add("event");

        localizer.SetLanguage("es");

        // The markup re-reads on Item[]; view models re-raise their own labels on LanguageChanged.
        // Both must see the new strings, and the bindings must not be told twice.
        Assert.Equal(["property:Item[]", "event"], order);
    }

    [Fact]
    public void SetLanguageToTheCurrentLanguageIsANoOp()
    {
        var localizer = new Localizer("es");
        var raised = 0;
        localizer.LanguageChanged += (_, _) => raised++;

        localizer.SetLanguage("es");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SetLanguageRoundTripsBackToEnglish()
    {
        var localizer = new Localizer();
        localizer.SetLanguage("es");

        localizer.SetLanguage("en");

        Assert.Equal("en", localizer.Current.Code);
        Assert.Equal("Connection", localizer[StringKeys.Settings.ConnectionTitle]);
    }

    [Fact]
    public void ALanguageRendersAsItsOwnNativeName()
        => Assert.Equal("Español", LanguageCatalog.ResolveOrDefault("es").ToString());
}

public class AppSettingsLanguageTests
{
    [Fact]
    public void DefaultsToEnglish()
        => Assert.Equal("en", new AppSettings().LanguageOrDefault());

    /// <summary>
    /// docs/PLAN-I18N.md §2.6 option A: a settings file written before this field existed
    /// deserializes to the default, and the interface switches to English once. Deliberate — there
    /// is no migration branch preserving the previous Spanish-only build's appearance.
    /// </summary>
    [Fact]
    public void ASettingsFileWithNoLanguageFieldReadsAsEnglish()
        => Assert.Equal("en", new AppSettings { Language = string.Empty }.LanguageOrDefault());

    [Fact]
    public void AnUnrecognisedLanguageDegradesRatherThanThrows()
        => Assert.Equal("en", new AppSettings { Language = "tlh" }.LanguageOrDefault());

    [Fact]
    public void AKnownLanguageSurvivesTheRoundTrip()
        => Assert.Equal("es", new AppSettings { Language = "es" }.LanguageOrDefault());
}

/// <summary>
/// docs/PLAN-I18N.md §6.3's third case: a message that stays on screen has to store its key, not
/// its rendered text, or it stays frozen in whatever language it was written in.
/// </summary>
public class LocalizedTextTests
{
    [Fact]
    public void AKeyedMessageRendersThroughTheStringTable()
        => Assert.Equal("Upload cancelled.", LocalizedText.Of(StringKeys.Status.UploadCancelled).Render());

    [Fact]
    public void ArgumentsAreSubstituted()
        => Assert.Equal("Renamed a to b.", LocalizedText.Of(StringKeys.Status.RenameDone, "a", "b").Render());

    [Fact]
    public void APluralMessagePutsTheCountFirst()
        => Assert.Equal("Uploaded 2 files to /x.", LocalizedText.Plural(StringKeys.Status.UploadDone, 2, "/x").Render());

    [Fact]
    public void VerbatimTextIsNotLookedUp()
    {
        var text = LocalizedText.Verbatim(StringKeys.Status.UploadCancelled);

        Assert.Null(text.Key);
        Assert.Equal(StringKeys.Status.UploadCancelled, text.Render());
    }

    [Fact]
    public void NoneIsEmptyAndRendersEmpty()
    {
        Assert.True(LocalizedText.None.IsEmpty);
        Assert.Equal(string.Empty, LocalizedText.None.Render());
    }

    [Fact]
    public void VerbatimNullIsEmpty()
        => Assert.True(LocalizedText.Verbatim(null).IsEmpty);

    /// <summary>
    /// Equality compares what would be shown. Two instances built from the same key allocate
    /// different <c>params</c> arrays, and reference-comparing those would report a change on
    /// every assignment — which is what <c>SetProperty</c> uses to decide whether to notify.
    /// </summary>
    [Fact]
    public void TwoMessagesWithTheSameKeyAndArgumentsAreEqual()
        => Assert.Equal(
            LocalizedText.Of(StringKeys.Status.RenameDone, "a", "b"),
            LocalizedText.Of(StringKeys.Status.RenameDone, "a", "b"));

    [Fact]
    public void DifferentArgumentsAreNotEqual()
        => Assert.NotEqual(
            LocalizedText.Of(StringKeys.Status.RenameDone, "a", "b"),
            LocalizedText.Of(StringKeys.Status.RenameDone, "a", "c"));
}
