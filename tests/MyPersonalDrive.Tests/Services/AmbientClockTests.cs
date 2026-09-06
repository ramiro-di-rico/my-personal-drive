using System.Text.RegularExpressions;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// AGENTS.md's rule: "Use <c>TimeProvider</c>, not <c>DateTime.Now</c> — tests substitute
/// <c>FakeTimeProvider</c>." The rule existed; nothing checked it, and seventeen call sites had
/// drifted past it (docs/PLAN-UX-ROUND-4.md Z4).
///
/// The two that mattered were the OAuth authenticators, where the ambient clock decided token
/// expiry and the refresh margin — so no test could cover a token expiring while the app is open,
/// or a margin off by a minute, which are the failures a user reads as being signed out for no
/// reason.
/// </summary>
public class AmbientClockTests
{
    /// <summary>
    /// Where reading the wall clock is not a decision. Both stamp a name — a crash log line, a
    /// backup file — and nothing branches on the value, so injecting a clock would add plumbing to
    /// paths that run while the process is dying or while settings.json is already corrupt, and buy
    /// no testability. Add an entry only with a reason of that shape.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "CrashLog.cs",           // timestamps a line in the last-resort log
        "AppSettingsService.cs", // names the backup taken when settings.json will not parse
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

    [Fact]
    public void NothingReadsTheAmbientClock()
    {
        var ambient = new Regex(@"\b(DateTime|DateTimeOffset)\.(Now|UtcNow)\b", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Allowed.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ambient.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {trimmed}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Take a TimeProvider and call GetUtcNow(). A call site that reads the wall clock directly\n" +
            "cannot be tested at a boundary — an expiry, a margin, a retry window — which is the only\n" +
            "reason this rule exists.\n\n  " + string.Join("\n  ", offenders));
    }
}
