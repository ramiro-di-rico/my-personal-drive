using System.Text.RegularExpressions;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace MyPersonalDrive.Tests.Views;

/// <summary>
/// Attaching a handler for a routing strategy an event does not route is silent: the call compiles,
/// the handler is stored, and it is never invoked. Nothing warns, and the feature simply does not
/// happen.
///
/// That is not hypothetical — docs/PLAN-UX-ROUND-3.md X2 shipped its double-click-to-open with
/// <c>RoutingStrategies.Tunnel</c>, copied from the pointer handlers beside it. PointerPressedEvent
/// routes Tunnel and Bubble; DoubleTappedEvent routes Bubble alone. So double click did nothing in
/// either pane and the context menu's "Open" was the only way to open a folder, which is how the
/// user found it rather than how a test did.
///
/// This reads the registrations out of the code-behind and checks each one against the event's own
/// declared strategies.
/// </summary>
public class RoutedEventRegistrationsTests
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
    /// <c>something.AddHandler(InputElement.XEvent, Handler, RoutingStrategies.Y)</c>, capturing X
    /// and every Y in the (possibly or-ed) strategy argument.
    /// </summary>
    private static readonly Regex Registration = new(
        @"AddHandler\(\s*InputElement\.(?<event>\w+)Event\s*,\s*\w+\s*,\s*(?<strategies>RoutingStrategies\.\w+(?:\s*\|\s*RoutingStrategies\.\w+)*)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryHandlerIsAttachedToAPhaseItsEventActuallyRoutes()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.axaml.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Registration.Matches(source))
            {
                var name = match.Groups["event"].Value;
                var field = typeof(InputElement).GetField($"{name}Event", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field?.GetValue(null) is not RoutedEvent routedEvent)
                {
                    // Not an InputElement event we can resolve — leave it alone rather than guess.
                    continue;
                }

                foreach (var token in match.Groups["strategies"].Value.Split('|', StringSplitOptions.TrimEntries))
                {
                    var requested = Enum.Parse<RoutingStrategies>(token["RoutingStrategies.".Length..]);
                    if (!routedEvent.RoutingStrategies.HasFlag(requested))
                    {
                        var line = source[..match.Index].Count(c => c == '\n') + 1;
                        offenders.Add(
                            $"{Path.GetFileName(file)}:{line}  {name} is attached for {requested}, " +
                            $"but the event only routes {routedEvent.RoutingStrategies} — the handler will never run.");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The specific pairing the bug turned on, pinned so the reasoning above stays checkable if a
    /// future Avalonia changes either event.
    /// </summary>
    [Fact]
    public void DoubleTappedRoutesBubbleOnly_WhilePointerPressedAlsoTunnels()
    {
        Assert.Equal(RoutingStrategies.Bubble, InputElement.DoubleTappedEvent.RoutingStrategies);
        Assert.True(InputElement.PointerPressedEvent.RoutingStrategies.HasFlag(RoutingStrategies.Tunnel));
    }
}
