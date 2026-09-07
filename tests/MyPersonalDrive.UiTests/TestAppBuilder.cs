using Avalonia;
using Avalonia.Headless;
using Xunit;
using MyPersonalDrive;
using MyPersonalDrive.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// One Avalonia application, one dispatcher, and windows that keep posting work to it after their
// test ends. Run two classes at once and one test's pump runs another's pending refresh, which
// empties the listing under it — four keyboard tests passed alone and failed together until this
// line existed. MyPersonalDrive.Tests turned parallelization off for the same reason, one
// process-wide singleton over (docs/PLAN-I18N.md's Localizer).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MyPersonalDrive.UiTests;

/// <summary>
/// The application these tests lay out windows inside. It is the app's own <see cref="App"/>, not a
/// stand-in: the styles, the theme dictionaries and the icon resources are exactly what ships, so a
/// measurement here is a measurement of the real thing.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
