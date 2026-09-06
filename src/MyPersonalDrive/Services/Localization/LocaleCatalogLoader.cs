using System.Reflection;
using System.Text.Json;

namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// Reads the embedded <c>Locales/*.json</c> files. Flat <c>string -&gt; string</c> maps, loaded
/// through <see cref="AppJsonContext"/> like every other serialized type in this codebase.
///
/// Deliberately not <c>ResourceManager</c>/<c>.resx</c>: satellite assemblies are resolved by
/// culture through reflection and assembly probing, which is exactly what <c>PublishAot</c> /
/// <c>TrimMode=partial</c> is hostile to (docs/PLAN-I18N.md §0.2).
/// </summary>
internal static class LocaleCatalogLoader
{
    private const string ResourcePrefix = "MyPersonalDrive.Services.Localization.Locales.";

    /// <summary>
    /// Loads one locale. Returns an empty map rather than throwing when the resource is absent or
    /// malformed: a broken locale must degrade to the English fallback, never take the app down at
    /// startup — the same spirit as <c>AppSettingsService.Load</c> quarantining a corrupt file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(LocaleCatalogLoader).GetTypeInfo().Assembly
                .GetManifestResourceStream(ResourcePrefix + code + ".json");
            if (stream is null)
            {
                return EmptyMap;
            }

            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.DictionaryStringString) ?? EmptyMap;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return EmptyMap;
        }
    }

    private static readonly Dictionary<string, string> EmptyMap = [];
}
