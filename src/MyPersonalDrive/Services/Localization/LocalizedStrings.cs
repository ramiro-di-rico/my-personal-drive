using System.Globalization;

namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// The string table as the markup sees it: <c>{Binding Loc[settings.general.title]}</c>.
///
/// This type exists for one reason, and it is a finding rather than a design preference. A
/// compiled binding whose path is <c>Loc</c> → <c>[key]</c> re-reads the indexer only when the
/// object the indexer is on is a *different* object. Raising <c>PropertyChanged("Item[]")</c> on a
/// long-lived <see cref="Localizer"/> does not move it: the markup renders correctly on load and
/// then never changes again, which is exactly the bug a live probe caught after L0 declared the
/// mechanism proven (docs/PLAN-I18N.md §3).
///
/// So <see cref="Localizer.SetLanguage"/> replaces this façade instead of mutating it. The binding
/// sees a new reference on the first node of the path, re-attaches, and re-reads every key.
/// Immutable and free to allocate — one object per language change, not per lookup.
/// </summary>
public sealed class LocalizedStrings
{
    private readonly Localizer _owner;

    internal LocalizedStrings(Localizer owner) => _owner = owner;

    /// <summary>What the markup binds to.</summary>
    public string this[string key] => _owner.T(key);

    /// <inheritdoc cref="Localizer.T"/>
    public string T(string key) => _owner.T(key);

    /// <inheritdoc cref="Localizer.F"/>
    public string F(string key, params object?[] args) => _owner.F(key, args);

    /// <inheritdoc cref="Localizer.Plural"/>
    public string Plural(string keyPrefix, int count, params object?[] args) => _owner.Plural(keyPrefix, count, args);

    /// <inheritdoc cref="Localizer.Culture"/>
    public CultureInfo Culture => _owner.Culture;

    /// <inheritdoc cref="Localizer.Current"/>
    public Language Current => _owner.Current;
}
