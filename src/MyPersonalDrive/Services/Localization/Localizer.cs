using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// The interface's string table, and the one place that knows what language is showing.
///
/// A singleton rather than a constructor dependency: the markup needs to reach it from every
/// <c>.axaml</c> file without threading it through each DataContext, and there is exactly one
/// interface language at a time by definition. ViewModels reach it through
/// <c>ObservableObject.Loc</c>, which is what the compiled bindings bind to.
///
/// Implements <see cref="INotifyPropertyChanged"/> directly rather than deriving from
/// <c>ViewModels.ObservableObject</c> — <c>Services/</c> does not depend on <c>ViewModels/</c>.
/// Changing the language raises <c>Item[]</c>, which is how every <c>{Binding Loc[key]}</c> in the
/// markup re-reads itself without a restart (docs/PLAN-I18N.md §3).
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>The reference locale, always loaded, always the fallback for a missing key.</summary>
    private readonly IReadOnlyDictionary<string, string> _fallback;

    private IReadOnlyDictionary<string, string> _strings;

    public static Localizer Instance { get; } = new();

    internal Localizer(string? initialCode = null)
    {
        _fallback = LocaleCatalogLoader.Load(LanguageCatalog.DefaultCode);
        Current = LanguageCatalog.ResolveOrDefault(initialCode);
        _strings = Current.Code == LanguageCatalog.DefaultCode
            ? _fallback
            : LocaleCatalogLoader.Load(Current.Code);
        Culture = ResolveCulture(Current.Code);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after <see cref="Current"/> and <see cref="Culture"/> have both been updated. What
    /// ViewModels subscribe to so they can re-raise their own derived labels.
    /// </summary>
    public event EventHandler? LanguageChanged;

    public Language Current { get; private set; }

    /// <summary>
    /// The culture for dates, numbers and byte sizes. Read this rather than
    /// <see cref="CultureInfo.CurrentCulture"/> at a formatting site, so the intent — "this is
    /// presentation" — is visible, and so machine data keeps using
    /// <see cref="CultureInfo.InvariantCulture"/> (docs/PLAN-I18N.md §10).
    /// </summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>
    /// The indexer the markup binds to: <c>{Binding Loc[settings.general.title]}</c>. Named
    /// <c>Item</c> explicitly so the change notification below matches what the binding listens for.
    /// </summary>
    [IndexerName("Item")]
    public string this[string key] => T(key);

    /// <summary>
    /// Looks up a key. Falls back to English, then — in a debug build — to a loud
    /// <c>⟦key⟧</c> so a missing string is caught while developing rather than shipped. Never throws
    /// and never returns null: a missing string must not be able to break a screen.
    /// </summary>
    public string T(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_strings.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value))
        {
            return value;
        }

#if DEBUG
        return "⟦" + key + "⟧";
#else
        return key;
#endif
    }

    /// <summary>
    /// A key with positional placeholders. Formats with <see cref="Culture"/>, so an embedded
    /// number or date follows the language being shown.
    /// </summary>
    public string F(string key, params object?[] args)
        => args.Length == 0 ? T(key) : string.Format(Culture, T(key), args);

    /// <summary>
    /// A count-sensitive key. Looks up <c>&lt;keyPrefix&gt;.&lt;category&gt;</c> for the current
    /// language's plural category, falling back to <c>.other</c>. <paramref name="count"/> is
    /// always <c>{0}</c>; anything else follows it.
    /// </summary>
    public string Plural(string keyPrefix, int count, params object?[] args)
    {
        var category = Current.PluralCategory(count);
        var key = keyPrefix + "." + category;
        if (!_strings.ContainsKey(key) && !_fallback.ContainsKey(key))
        {
            key = keyPrefix + ".other";
        }

        object?[] formatArgs = args.Length == 0 ? [count] : [count, .. args];
        return string.Format(Culture, T(key), formatArgs);
    }

    /// <summary>
    /// Switches the interface language. An unknown code resolves to English rather than throwing.
    /// Sets the process-wide default culture as well as <see cref="Culture"/> so that anything
    /// formatting through <c>CurrentCulture</c> follows — which is also why every parse of machine
    /// data has to be explicitly invariant (docs/PLAN-I18N.md §10).
    /// </summary>
    public void SetLanguage(string? code)
    {
        var language = LanguageCatalog.ResolveOrDefault(code);
        if (language.Code == Current.Code)
        {
            return;
        }

        Current = language;
        _strings = language.Code == LanguageCatalog.DefaultCode
            ? _fallback
            : LocaleCatalogLoader.Load(language.Code);
        Culture = ResolveCulture(language.Code);

        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;

        // Order matters: the markup's bindings re-read on Item[], and ViewModels re-raise their own
        // derived labels on LanguageChanged. Both must see the new strings already in place.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A code that <see cref="LanguageCatalog"/> accepts should always be a real culture, but an
    /// invariant-globalization or trimmed runtime can still refuse it — fall back rather than
    /// throw at startup.
    /// </summary>
    private static CultureInfo ResolveCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
