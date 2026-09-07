using System.Text.RegularExpressions;
using Xunit;

namespace MyPersonalDrive.Tests.Views;

/// <summary>
/// docs/PLAN-UX-ROUND-3.md X6. A colour literal in a view is a colour that cannot follow the theme,
/// and it is invisible in review because it looks like every other attribute — the same failure
/// shape the localization gate exists for, one dimension over.
///
/// The whole palette lives in <c>App.axaml</c>'s two <c>ThemeDictionaries</c>, so that file is the
/// only one allowed to name a colour. A view names a brush key.
/// </summary>
public class NoHardcodedColorsTests
{
    /// <summary>
    /// <c>#RGB</c>, <c>#RRGGBB</c> and <c>#AARRGGBB</c>, which is every form Avalonia accepts for a
    /// literal. Named colours ("Red") are matched separately below — <c>Transparent</c> is not one
    /// of them, since it is a structural value rather than a palette choice.
    /// </summary>
    private static readonly Regex HexColour = new(@"#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b", RegexOptions.Compiled);

    private static readonly Regex NamedColour = new(
        @"(?:Background|Foreground|Fill|Stroke|BorderBrush)\s*=\s*""(?<value>[A-Za-z]+)""",
        RegexOptions.Compiled);

    /// <summary>Structural values, not palette choices: they mean "no paint", not "this colour".</summary>
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "Transparent",
    };

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

    private static IEnumerable<string> ViewFiles()
    {
        var root = RepositoryRoot();
        return Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFileName(file), "App.axaml", StringComparison.Ordinal));
    }

    [Fact]
    public void NoViewNamesAColourDirectly()
    {
        var offenders = new List<string>();

        foreach (var file in ViewFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in HexColour.Matches(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {match.Value}");
                }

                foreach (Match match in NamedColour.Matches(lines[i]))
                {
                    var value = match.Groups["value"].Value;
                    if (!AllowedNames.Contains(value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {value}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Views must reference a brush key — {DynamicResource SomeBrush} — so the colour follows the\n" +
            "theme. Add the brush to BOTH ThemeDictionaries in App.axaml; a brush defined in only one of\n" +
            "them is a bug that shows up for half the users.\n\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A brush that exists in one dictionary and not the other renders as an unresolved resource on
    /// that theme — which is precisely the class of defect this pair of tests exists to prevent, so
    /// leaving it to review would miss the point.
    /// </summary>
    [Fact]
    public void EveryBrushIsDefinedInBothThemeDictionaries()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MyPersonalDrive", "App.axaml"));

        var dictionaries = Regex
            .Matches(app, @"<ResourceDictionary x:Key=""(?<theme>Light|Dark)"">(?<body>.*?)</ResourceDictionary>", RegexOptions.Singleline)
            .ToDictionary(
                match => match.Groups["theme"].Value,
                match => Regex
                    .Matches(match.Groups["body"].Value, @"x:Key=""(?<key>[^""]+)""")
                    .Select(key => key.Groups["key"].Value)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        Assert.Equal(2, dictionaries.Count);
        Assert.Equal(dictionaries["Light"].OrderBy(key => key, StringComparer.Ordinal), dictionaries["Dark"].OrderBy(key => key, StringComparer.Ordinal));
    }
}
