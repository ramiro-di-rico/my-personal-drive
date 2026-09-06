using Xunit;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Tests.Services.Localization;

/// <summary>
/// The safety net that makes adding a language a data change: if every locale is provably
/// key-for-key identical to English, a translator cannot half-finish one and ship a blank screen.
/// See docs/PLAN-I18N.md §2.7 and .claude/skills/add-language/SKILL.md.
/// </summary>
public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{\d+", RegexOptions.Compiled);

    internal static IReadOnlyDictionary<string, string> LoadLocale(string code) => Load(code);

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        var loader = typeof(Localizer).Assembly
            .GetType("MyPersonalDrive.Services.Localization.LocaleCatalogLoader", throwOnError: true)!;
        var load = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
        return (IReadOnlyDictionary<string, string>)load.Invoke(null, [code])!;
    }

    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in LanguageCatalog.Available)
        {
            data.Add(language.Code);
        }

        return data;
    }

    [Fact]
    public void EnglishIsTheReferenceLocaleAndIsNotEmpty()
    {
        Assert.Equal("en", LanguageCatalog.DefaultCode);
        Assert.NotEmpty(Load("en"));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryLocaleHasTheSameKeysAsEnglish(string code)
    {
        var english = Load("en").Keys.ToHashSet(StringComparer.Ordinal);
        var locale = Load(code);

        Assert.False(locale.Count == 0, $"{code}.json is missing or empty — is it an EmbeddedResource?");

        var missing = english.Except(locale.Keys).Order().ToList();
        var extra = locale.Keys.Except(english).Order().ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"{code}.json is out of step with en.json.\n" +
            $"  Missing ({missing.Count}): {string.Join(", ", missing)}\n" +
            $"  Not in English ({extra.Count}): {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void NoLocaleValueIsEmptyOrWhitespace(string code)
    {
        var blank = Load(code).Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).Order();
        Assert.True(!blank.Any(), $"{code}.json has blank values: {string.Join(", ", blank)}");
    }

    /// <summary>
    /// A translator dropping a <c>{0}</c> is a crash, not a typo: <see cref="string.Format(IFormatProvider, string, object?[])"/>
    /// throws when the format string references an argument that was supplied, and renders nothing
    /// useful when it doesn't. Their *order* may differ between languages — positional formatting
    /// allows that — so this compares sets, not sequences.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void PlaceholderSetsMatchEnglishPerKey(string code)
    {
        var english = Load("en");
        var mismatched = new List<string>();

        foreach (var (key, value) in Load(code))
        {
            if (!english.TryGetValue(key, out var reference))
            {
                continue;
            }

            var expected = Placeholder.Matches(reference).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            var actual = Placeholder.Matches(value).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(actual))
            {
                mismatched.Add($"{key}: en has [{string.Join(",", expected.Order())}], {code} has [{string.Join(",", actual.Order())}]");
            }
        }

        Assert.True(mismatched.Count == 0, string.Join("\n", mismatched));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryPluralKeyHasBothOneAndOther(string code)
    {
        var keys = Load(code).Keys.ToHashSet(StringComparer.Ordinal);
        var incomplete = keys
            .Where(key => key.EndsWith(".one", StringComparison.Ordinal))
            .Select(key => key[..^4])
            .Where(prefix => !keys.Contains(prefix + ".other"))
            .Order();

        Assert.True(!incomplete.Any(), $"{code}.json has .one without .other: {string.Join(", ", incomplete)}");
    }

    /// <summary>
    /// The constants and the reference locale are one vocabulary. Without this, a key can be added
    /// to <c>en.json</c> and never referenced, or referenced through a constant whose value has a
    /// typo — and a typo'd key renders as the fallback marker at runtime, not at build time.
    /// </summary>
    [Fact]
    public void StringKeysConstantsAreExactlyTheEnglishKeySet()
    {
        var constants = CollectConstants(typeof(StringKeys)).ToHashSet(StringComparer.Ordinal);
        var english = Load("en").Keys.ToHashSet(StringComparer.Ordinal);

        // A plural constant names the *prefix* — "console.activeoperations" — while the locale
        // holds "…​.one" and "…​.other". Count the prefix as covered when its categories exist, and
        // the categories as covered by that one constant.
        // A ".other" alone is not a plural — "filekind.other" and "sync.skip.unspecified" are the
        // default case of a category. Only a key whose ".one" sibling exists too is one.
        var pluralPrefixes = english
            .Where(key => key.EndsWith(".other", StringComparison.Ordinal) && english.Contains(key[..^6] + ".one"))
            .Select(key => key[..^6])
            .Where(constants.Contains)
            .ToHashSet(StringComparer.Ordinal);

        var missingConstant = english
            .Where(key => !constants.Contains(key))
            .Where(key => !pluralPrefixes.Any(prefix => key.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .Order().ToList();
        var danglingConstant = constants.Except(english).Except(pluralPrefixes).Order().ToList();

        Assert.True(
            missingConstant.Count == 0 && danglingConstant.Count == 0,
            $"StringKeys and en.json disagree.\n" +
            $"  In en.json with no constant ({missingConstant.Count}): {string.Join(", ", missingConstant)}\n" +
            $"  Constants with no string ({danglingConstant.Count}): {string.Join(", ", danglingConstant)}");
    }

    private static IEnumerable<string> CollectConstants(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (field is { IsLiteral: true, IsInitOnly: false } && field.GetRawConstantValue() is string value)
            {
                yield return value;
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
        {
            foreach (var value in CollectConstants(nested))
            {
                yield return value;
            }
        }
    }
}
