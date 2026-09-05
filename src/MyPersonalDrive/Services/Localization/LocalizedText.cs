namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// A string that has not been rendered yet — a key plus its arguments, resolved through
/// <see cref="Localizer"/> at the moment it is read.
///
/// Needed for messages that stay on screen: a status line saying "Uploaded 3 files" is written
/// once and then sits there indefinitely, so storing the *rendered* sentence freezes it in
/// whatever language was current when the operation finished. Storing the key instead means the
/// line follows the language picker like everything else (docs/PLAN-I18N.md §6.3).
///
/// <see cref="Verbatim"/> covers the other half: text that is already final — a provider's own
/// error sentence, a path, something a caller outside the view model computed — and must be shown
/// as-is rather than looked up.
/// </summary>
public readonly struct LocalizedText : IEquatable<LocalizedText>
{
    private readonly string? _literal;
    private readonly object?[]? _args;
    private readonly int? _pluralCount;

    private LocalizedText(string? key, string? literal, object?[]? args, int? pluralCount)
    {
        Key = key;
        _literal = literal;
        _args = args;
        _pluralCount = pluralCount;
    }

    /// <summary>The key this renders through, or null when it is <see cref="Verbatim"/> text. Tests assert on this rather than on prose.</summary>
    public string? Key { get; }

    public static LocalizedText None { get; } = new(null, null, null, null);

    public static LocalizedText Of(string key, params object?[] args) => new(key, null, args, null);

    /// <summary>A count-sensitive key; <paramref name="count"/> is <c>{0}</c>, as in <see cref="Localizer.Plural"/>.</summary>
    public static LocalizedText Plural(string keyPrefix, int count, params object?[] args)
        => new(keyPrefix, null, args, count);

    /// <summary>Text that is already final and must not be looked up.</summary>
    public static LocalizedText Verbatim(string? text) => new(null, text, null, null);

    public bool IsEmpty => Key is null && string.IsNullOrEmpty(_literal);

    public string Render()
    {
        if (Key is null)
        {
            return _literal ?? string.Empty;
        }

        var localizer = Localizer.Instance;
        return _pluralCount is { } count
            ? localizer.Plural(Key, count, _args ?? [])
            : localizer.F(Key, _args ?? []);
    }

    public override string ToString() => Render();

    /// <summary>
    /// Compares what would be shown, not the arguments array — two instances built from the same
    /// key and the same values are the same message, and reference-comparing their
    /// freshly-allocated <c>params</c> arrays would say otherwise on every single assignment.
    /// </summary>
    public bool Equals(LocalizedText other) => string.Equals(Render(), other.Render(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is LocalizedText other && Equals(other);

    public override int GetHashCode() => Render().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(LocalizedText left, LocalizedText right) => left.Equals(right);

    public static bool operator !=(LocalizedText left, LocalizedText right) => !left.Equals(right);
}
