using System.Text.RegularExpressions;
using Xunit;

namespace MyPersonalDrive.Tests.Views;

/// <summary>
/// docs/PLAN-UX-ROUND-3.md X4. A button whose entire content is a <c>Path</c> has no text for an
/// automation client to fall back on, so without <c>AutomationProperties.Name</c> it is announced
/// as "button" and nothing else. <c>ToolTip.Tip</c> does not close that gap: a tooltip is a pointer
/// affordance.
///
/// The gate is deliberately narrow — icon-only buttons, which are the population that was entirely
/// unnamed — rather than every control, so it fails for a real reason rather than as background
/// noise a future change learns to suppress.
/// </summary>
public class IconOnlyControlsAreNamedTests
{
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

    /// <summary>
    /// Every <c>&lt;Button …&gt;…&lt;/Button&gt;</c> in the file, start tag and content separated.
    /// Buttons nest in this markup (a row's action buttons sit inside a row Button in the local
    /// pane), so this counts depth rather than matching the first closing tag.
    /// </summary>
    private static IEnumerable<(int Line, string Head, string Inner)> Buttons(string markup)
    {
        foreach (Match open in Regex.Matches(markup, @"<Button(?=[\s>/])"))
        {
            var headEnd = EndOfTag(markup, open.Index);
            var head = markup[open.Index..headEnd];
            if (head.EndsWith("/>", StringComparison.Ordinal))
            {
                yield return (markup[..open.Index].Count(c => c == '\n') + 1, head, string.Empty);
                continue;
            }

            var depth = 1;
            var cursor = headEnd;
            while (depth > 0)
            {
                var next = Regex.Match(markup[cursor..], @"<(?<close>/?)Button(?=[\s>/])");
                if (!next.Success)
                {
                    break;
                }

                var at = cursor + next.Index;
                if (next.Groups["close"].Value.Length > 0)
                {
                    depth--;
                    cursor = markup.IndexOf('>', at) + 1;
                    continue;
                }

                var innerEnd = EndOfTag(markup, at);
                if (!markup[at..innerEnd].EndsWith("/>", StringComparison.Ordinal))
                {
                    depth++;
                }

                cursor = innerEnd;
            }

            yield return (markup[..open.Index].Count(c => c == '\n') + 1, head, markup[headEnd..cursor]);
        }
    }

    /// <summary>End of a start tag, skipping any '&gt;' that appears inside an attribute value.</summary>
    private static int EndOfTag(string markup, int start)
    {
        char? quote = null;
        for (var i = start; i < markup.Length; i++)
        {
            var c = markup[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        return markup.Length;
    }

    [Fact]
    public void EveryIconOnlyButtonCarriesAnAccessibleName()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.axaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(file);
            foreach (var (line, head, inner) in Buttons(markup))
            {
                var iconOnly = inner.Contains("<Path", StringComparison.Ordinal)
                    && !inner.Contains("<TextBlock", StringComparison.Ordinal)
                    && !head.Contains("Content=", StringComparison.Ordinal);

                if (iconOnly && !head.Contains("AutomationProperties.Name", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{line}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An icon-only button announces itself as \"button\" unless it carries\n" +
            "AutomationProperties.Name. Bind it to the same value as ToolTip.Tip so the two cannot drift.\n\n  "
            + string.Join("\n  ", offenders));
    }
}
