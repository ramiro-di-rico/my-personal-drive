using Avalonia;
using Avalonia.Headless;
using MyPersonalDrive;
using MyPersonalDrive.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

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
