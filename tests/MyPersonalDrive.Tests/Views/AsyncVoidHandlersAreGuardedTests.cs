using System.Text.RegularExpressions;
using Xunit;

namespace MyPersonalDrive.Tests.Views;

/// <summary>
/// AGENTS.md's own non-negotiable: "`async void` kills the process." <see cref="AsyncCommand"/>
/// enforces it for commands, and docs/PLAN-TECH-DEBT.md B0.1 is the batch that did so. Event
/// handlers are the half that rule never covered — they are not commands, so nothing routes their
/// exceptions anywhere, and an exception escaping one ends the process with a crash.log entry.
///
/// Four of the seven in <c>MainWindow.axaml.cs</c> had no guard at all: both drag-and-drop handlers
/// (an upload fails for a dozen ordinary reasons) and both file-picker handlers (the desktop portal
/// can fail, and the assignment afterwards writes settings.json). See docs/PLAN-UX-ROUND-4.md Z1.
///
/// The check is textual because the property is textual: a body that contains no `catch` cannot
/// contain the exception. It does not attempt to judge whether the catch is the right one.
/// </summary>
public class AsyncVoidHandlersAreGuardedTests
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

    /// <summary>The body of a method, by counting braces from its opening one.</summary>
    private static string BodyAt(string[] lines, int start)
    {
        var depth = 0;
        var opened = false;
        var body = new List<string>();

        for (var i = start; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            opened |= lines[i].Contains('{', StringComparison.Ordinal);
            body.Add(lines[i]);

            if (opened && depth == 0)
            {
                break;
            }
        }

        return string.Join('\n', body);
    }

    [Fact]
    public void EveryAsyncVoidHandlerContainsItsOwnExceptions()
    {
        var declaration = new Regex(@"\basync void (?<name>\w+)\s*\(", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // AsyncCommand.Execute is the one that is allowed to be bare: it *is* the routing,
                // and ExecuteAsync inside it catches everything (PLAN-TECH-DEBT.md B0.1).
                if (Path.GetFileName(file) == "AsyncCommand.cs")
                {
                    continue;
                }

                var match = declaration.Match(lines[i]);
                if (!match.Success || lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!BodyAt(lines, i).Contains("catch", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {match.Groups["name"].Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An async void method has nowhere to put an exception: one that escapes terminates the\n" +
            "process. Wrap the body and route the failure — MainWindowViewModel.ReportHandlerFailure\n" +
            "is the same sink AsyncCommand uses.\n\n  " + string.Join("\n  ", offenders));
    }
}
