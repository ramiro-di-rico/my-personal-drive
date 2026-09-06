namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// The languages this build ships. Deliberately shaped like
/// <see cref="Providers.ProviderCatalog"/> — same "known list plus degrade to a default rather
/// than throw" contract, for the same reason: a <c>settings.json</c> naming a language written by
/// a newer build must not crash an older one.
///
/// Adding a language is one row here plus a <c>Locales/&lt;code&gt;.json</c> file; the locale
/// files are globbed as embedded resources, so no <c>.csproj</c> edit either. See
/// <c>.claude/skills/add-language/SKILL.md</c>.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>
    /// English, and also the reference locale: every key exists in <c>en.json</c> first, and a key
    /// missing from another locale falls back here rather than rendering blank.
    /// </summary>
    public const string DefaultCode = "en";

    public static IReadOnlyList<Language> Available { get; } =
    [
        new Language("en", "English", "English"),
        new Language("es", "Spanish", "Español"),
        new Language("it", "Italian", "Italiano"),
    ];

    public static Language Default { get; } = Available[0];

    /// <summary>Never throws. An unknown, empty or null code resolves to <see cref="Default"/>.</summary>
    public static Language ResolveOrDefault(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Default;
        }

        foreach (var language in Available)
        {
            if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return Default;
    }
}
