using System.Text.RegularExpressions;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Localization;

/// <summary>
/// docs/PLAN-I18N.md L9. The gate that stops the sweep from unravelling: a literal in the markup
/// is a string that will not follow the language picker, and it is invisible in review because it
/// looks like every other attribute.
///
/// This ships last on purpose. A gate that has to arrive with a four-hundred-entry allowlist
/// teaches nothing; the allowlist below is short enough to read, and every entry in it is a
/// deliberate decision rather than a backlog.
/// </summary>
public class NoHardcodedStringsTests
{
    private static readonly Regex LocalizableAttribute = new(
        @"(?<attr>Text|Content|PlaceholderText|Watermark|Header|ToolTip\.Tip)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Everything the gate accepts as a literal, and why. Proper nouns and glyphs — nothing that
    /// changes between languages.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "DRIVE",          // the app's own wordmark
        "Proton Drive",   // provider names: proper nouns, and the CLI's own spelling
        "OneDrive",
        "Google Drive",
        "Nextcloud",
        "Custom S3",
        "·",              // separator glyph
    };

    private static IEnumerable<string> AxamlFiles()
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void NoMarkupCarriesALiteralUserFacingString()
    {
        var offenders = new List<string>();

        foreach (var file in AxamlFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in LocalizableAttribute.Matches(lines[i]))
                {
                    var value = match.Groups["value"].Value;

                    // A binding, a resource, or an empty attribute is not a literal.
                    if (value.Length == 0 || value.StartsWith('{') || Allowed.Contains(value))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {match.Groups["attr"].Value}=\"{value}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Markup must bind these through the string table — {Binding Loc[some.key]} — or the text will\n" +
            "not follow the language picker. If a value genuinely never changes between languages, add it\n" +
            "to the allowlist in this test with a reason.\n\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A <c>StringFormat</c> inside a binding is a literal that does not look like one — which is
    /// exactly how "{0} free" survived the round that made the whole interface Spanish. Numeric and
    /// date format strings are fine; text is not.
    /// </summary>
    [Fact]
    public void NoBindingCarriesALiteralStringFormat()
    {
        var offenders = new List<string>();
        // Quoted ('{}{0} free') or bare ({}{0:P0}) — the bare form still has to swallow its own
        // closing brace, which is why it is not simply "everything up to a }".
        var stringFormat = new Regex(@"StringFormat=(?<value>'[^']*'|\{\}\{[^}]*\}[^,}]*)", RegexOptions.Compiled);

        foreach (var file in AxamlFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in stringFormat.Matches(lines[i]))
                {
                    var value = match.Groups["value"].Value.Trim('\'');

                    // "{}{0:P0}", "{}{0:N0} KB/s" — a format specifier and at most a unit symbol.
                    if (Regex.IsMatch(value, @"^\{\}\{\d+(:[^}]*)?\}\s*[\w/%]*$"))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  StringFormat={match.Groups["value"].Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A StringFormat carrying words is an untranslatable string hiding inside a binding, and it\n" +
            "cannot express plural agreement either. Move it to a view-model property that goes through\n" +
            "the string table.\n\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A localization binding has to be able to tell the markup it changed. Binding one to a
    /// static source cannot: <c>{x:Static loc:Localizer.Instance}</c> has no PropertyChanged the
    /// binding will honour, so the text renders once and then stays in whatever language was
    /// current at load. That is the bug this whole gate family exists to prevent, and it shipped
    /// once already (docs/PLAN-I18N.md §3).
    ///
    /// Reach the string table through a view model — <c>{Binding Loc[key]}</c>, or
    /// <c>$parent[Window]</c> when the DataContext is a model type.
    /// </summary>
    [Fact]
    public void NoLocalizationBindingUsesAStaticSource()
    {
        var offenders = new List<string>();

        foreach (var file in AxamlFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("x:Static", StringComparison.Ordinal)
                    && lines[i].Contains("Localizer", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A binding onto Localizer.Instance as a static source cannot be notified, so its text\n" +
            "freezes at whatever language was current when the view loaded. Bind through a view\n" +
            "model's Loc instead.\n\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The counterpart to the markup gate, for code. Every user-facing sentence in <c>src/</c> now
    /// lives in the locale files; a Spanish sentence anywhere else means someone wrote copy at a
    /// call site instead of adding a key — which is how the interface ended up single-language in
    /// the first place (PLAN-UX-ROUND-2 U4).
    ///
    /// A heuristic, deliberately: it looks for Spanish function words and Spanish-only characters
    /// rather than trying to detect language properly. It is tuned to have no false positives on
    /// the current tree; if it ever fires on something legitimate, widen <c>Allowed</c> above with
    /// a reason rather than weakening the pattern.
    /// </summary>
    [Fact]
    public void NoSourceFileCarriesASpanishSentence()
    {
        // Function words that do not appear as standalone words in English source, plus the
        // characters no English string has. Two function words, or one accented character, is the
        // threshold — one "la" could be a variable name in a string; two is prose.
        var functionWord = new Regex(
            @"\b(el|la|los|las|del|una|para|que|sin|con|ya|más|está|esta|por|se|un|de)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var spanishOnly = new Regex(@"[áéíóúñ¿¡Á-Ú]", RegexOptions.Compiled);
        var literal = new Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        var offenders = new List<string>();
        var root = Path.Combine(RepositoryRoot(), "src");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // The locale files' own directory is where Spanish belongs.
            if (file.Contains(Path.Combine("Localization", "Locales"), StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                {
                    continue;
                }

                foreach (Match match in literal.Matches(lines[i]))
                {
                    var value = match.Groups[1].Value;
                    if (value.Length < 8)
                    {
                        continue;
                    }

                    if (functionWord.Matches(value).Count >= 2 || spanishOnly.IsMatch(value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}  \"{value}\"");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "User-facing copy belongs in Locales/*.json, reached through a StringKeys constant — not\n" +
            "written at the call site. An exception message needs both: an English Message for the\n" +
            "console and crash log, and a LocalizedText Detail for the screen (ILocalizedError).\n\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A smell test for copy that was pasted between locales rather than translated. Only values
    /// that are genuinely language-neutral should be byte-identical.
    /// </summary>
    [Fact]
    public void SpanishAndEnglishOnlyMatchWhereTheyShould()
    {
        var english = LocalizationTests.LoadLocale("en");
        var spanish = LocalizationTests.LoadLocale("es");

        var identical = english
            .Where(pair => spanish.TryGetValue(pair.Key, out var other) && string.Equals(pair.Value, other, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Where(key => !LanguageNeutral(key))
            .Order()
            .ToList();

        Assert.True(
            identical.Count == 0,
            "These keys read the same in both locales, which usually means the value was copied rather\n" +
            "than translated. If a value really is language-neutral (a pure layout template, a unit, a\n" +
            "proper noun), add it to LanguageNeutral in this test.\n\n  " + string.Join("\n  ", identical));
    }

    /// <summary>Keys whose value is a layout template, a symbol or a proper noun, not prose.</summary>
    private static bool LanguageNeutral(string key) => key is
        "common.bytes"                    // "{0} bytes" — the unit is the same word
        or "common.no"                    // "No" / "No"
        or "common.ok"
        or "dialog.preview.action"        // "{0}  {1}"
        or "dialog.preview.warning"       // "⛔ {0}"
        or "dialog.properties.field"      // "{0}: {1}"
        or "dialog.remotebrowser.folder"  // "📁 {0}"
        or "filekind.audio"
        or "filekind.pdf"
        or "metrics.depthnote"            // "{0} · {1}"
        or "metrics.headline"             // "{0} · {1}"
        or "quota.atleast"
        or "quota.summary"
        or "quota.unknown"
        or "status.download.itemerror"    // "{0}: {1}"
        or "sync.exec.progress"
        or "sync.failure.summary"
        or "sync.filter.label"
        or "sync.progress"
        or "sync.status.error"
        or "transfer.failed"              // "Error" / "Error"
        or "viewer.note.bytes"
        or "viewer.zoom.label";           // "Zoom:" / "Zoom:"
}
