using System.Reflection;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The general form of a defect that has now shipped four separate times: a localized string
/// computed once and stored, which then stays in whichever language was current when it was
/// written. The chips did it (docs/PLAN-UX-ROUND-3.md X8), and six more properties were doing it
/// underneath — found by comparing, not by reading (docs/PLAN-UX-ROUND-4.md Y7).
///
/// So this test does the comparison mechanically. Build the view model in English and switch it to
/// Spanish; build a second one in Spanish from the start; every public string property has to
/// agree. A property that differs is one the language picker did not reach.
///
/// It cannot be fooled by a property that happens to read correctly, and it needs no list of what
/// to check — which is the point, because every previous attempt at this class of bug was a list
/// someone had to remember to extend.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class LanguageSwitchStalenessTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Staleness").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-stale-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Empty, and worth keeping empty. It held CliVersion and CliUpdateStatus while their fix — a
    /// LocalizedText each, threaded through the CLI self-update flow — was tracked as
    /// docs/PLAN-UX-ROUND-4.md Y7. Do not add an entry without a tracked reason: an allowlist is
    /// how a gate stops being one.
    /// </summary>
    private static readonly HashSet<string> KnownStale = new(StringComparer.Ordinal);

    public LanguageSwitchStalenessTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Localizer.Instance.SetLanguage(LanguageCatalog.DefaultCode);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private MainWindowViewModel Build()
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var store = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));

        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store)));
    }

    /// <summary>
    /// Every readable string on the view model *and* on the child view models it owns, keyed by
    /// "Child.Property".
    ///
    /// The children are not an optional extra. CliVersion and CliUpdateStatus were on
    /// MainWindowViewModel when this gate was written and moved to CliUpdateViewModel in
    /// docs/PLAN-UX-ROUND-4.md Z5 step 1 — a gate that only looked at the parent would have gone on
    /// passing while quietly covering two properties fewer. Extracting a cluster must not shrink
    /// what is checked.
    /// </summary>
    private static Dictionary<string, string> ReadableStrings(MainWindowViewModel viewModel)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(viewModel, prefix: string.Empty);
        return values;

        void Collect(ObservableObject target, string prefix)
        {
            foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                {
                    continue;
                }

                if (property.PropertyType == typeof(string))
                {
                    values[prefix + property.Name] = Read(target, property);
                    continue;
                }

                // One level down, into the children this view model owns. Deeper would reach the
                // listing rows, which are rebuilt per folder and have nothing stable to compare.
                if (prefix.Length == 0
                    && typeof(ObservableObject).IsAssignableFrom(property.PropertyType)
                    && Read(target, property) is not null
                    && property.GetValue(target) is ObservableObject child)
                {
                    Collect(child, property.Name + ".");
                }
            }
        }

        static string Read(object target, PropertyInfo property)
        {
            try
            {
                return property.PropertyType == typeof(string)
                    ? (string?)property.GetValue(target) ?? string.Empty
                    : string.Empty;
            }
            catch (TargetInvocationException)
            {
                return string.Empty;
            }
        }
    }

    [Fact]
    public void EveryStringProperty_ReadsTheSameAfterASwitchAsItDoesFromAFreshStart()
    {
        Localizer.Instance.SetLanguage("en");
        var switched = Build();

        Localizer.Instance.SetLanguage("es");
        var fresh = Build();

        var afterSwitch = ReadableStrings(switched);
        var fromScratch = ReadableStrings(fresh);

        var stale = afterSwitch.Keys
            .Where(name => !KnownStale.Contains(name))
            .Where(name => !string.Equals(afterSwitch[name], fromScratch[name], StringComparison.Ordinal))
            .Select(name => $"{name}: after a switch \"{afterSwitch[name]}\", from a fresh start \"{fromScratch[name]}\"")
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These properties did not follow the language picker. A localized string that is computed\n" +
            "once and stored stays in the language it was written in — read it through Loc at get time,\n" +
            "or keep a LocalizedText and re-render it in OnLanguageChanged.\n\n  " + string.Join("\n  ", stale));
    }
}
