using Xunit;

namespace MyPersonalDrive.Tests;

/// <summary>
/// Serializes the test classes that redirect <c>XDG_CONFIG_HOME</c>.
///
/// That variable is process-global, and xUnit runs test classes in parallel: two classes swapping
/// it at once means one of them reads the other's temp directory — or worse, the developer's real
/// <c>~/.config/MyPersonalDrive</c> during the window where it has been restored. That produced a
/// failure that only appeared in the full run and passed in isolation, which is the worst kind.
///
/// Any new test class that touches <see cref="MyPersonalDrive.Services.AppSettingsService"/> (or
/// anything that constructs one, such as <see cref="MyPersonalDrive.ViewModels.MainWindowViewModel"/>)
/// belongs in this collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AppDataCollection
{
    public const string Name = "app-data (serialized: XDG_CONFIG_HOME is process-global)";
}
