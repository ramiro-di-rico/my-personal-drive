namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// One interface language. <paramref name="Code"/> is a BCP-47 tag that <see cref="System.Globalization.CultureInfo"/>
/// recognises — it is the locale file's name, the value persisted in <c>AppSettings.Language</c>,
/// and the culture used to format dates and numbers, so the three cannot drift apart.
/// </summary>
/// <param name="Code">"en", "es".</param>
/// <param name="EnglishName">For logs and the CLI console, which stay English by decision (docs/PLAN-I18N.md §9).</param>
/// <param name="NativeName">What the Settings picker shows — a speaker scans for their own language's name for itself.</param>
public sealed record Language(string Code, string EnglishName, string NativeName)
{
    /// <summary>
    /// Which plural key suffix a count selects. The default covers every language this app ships
    /// (one/other); a language with CLDR few/many/zero categories supplies its own here, which is
    /// the only code change adding such a language needs. See
    /// <c>.claude/skills/add-language/SKILL.md</c>.
    /// </summary>
    public Func<int, string> PluralCategory { get; init; } = static count => count == 1 ? "one" : "other";

    /// <summary>Shown by the Settings picker. A record's generated ToString would dump every field.</summary>
    public override string ToString() => NativeName;
}
