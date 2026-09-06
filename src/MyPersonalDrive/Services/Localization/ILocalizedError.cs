namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// An exception that carries its own translatable sentence alongside
/// <see cref="Exception.Message"/>, so the interface can show one and the console and crash log
/// can keep the other.
///
/// This is the resolution of the tension in docs/PLAN-I18N.md §9: the console and the crash log
/// want a stable, greppable English sentence that never moves with the user's language, while the
/// screen wants the user's language. Those are two different requirements on one string, so the
/// exception carries both — <c>Message</c> stays English, <see cref="Detail"/> is the key.
///
/// <see cref="LocalizedText.None"/> means "no sentence of our own": the message quotes a provider
/// or the CLI verbatim, and the interface should show it as-is.
/// </summary>
public interface ILocalizedError
{
    LocalizedText Detail { get; }
}

/// <summary>Reading whichever sentence a given exception can offer the user.</summary>
public static class LocalizedErrorExtensions
{
    /// <summary>
    /// The exception's own translated sentence when it has one, otherwise its
    /// <see cref="Exception.Message"/> verbatim — which for a provider failure is the provider's
    /// own words, and must not be paraphrased.
    /// </summary>
    public static LocalizedText DescribeForUser(this Exception exception)
        => exception is ILocalizedError { Detail.IsEmpty: false } localized
            ? localized.Detail
            : LocalizedText.Verbatim(exception.Message);
}

/// <summary>An <see cref="IOException"/> that also carries a translated sentence. See <see cref="ILocalizedError"/>.</summary>
public sealed class LocalizedIOException(string message, LocalizedText detail)
    : IOException(message), ILocalizedError
{
    public LocalizedText Detail { get; } = detail;
}

/// <summary>A <see cref="FileNotFoundException"/> that also carries a translated sentence.</summary>
public sealed class LocalizedFileNotFoundException(string message, LocalizedText detail, string? fileName = null)
    : FileNotFoundException(message, fileName), ILocalizedError
{
    public LocalizedText Detail { get; } = detail;
}

/// <summary>An <see cref="InvalidOperationException"/> that also carries a translated sentence.</summary>
public sealed class LocalizedInvalidOperationException(string message, LocalizedText detail)
    : InvalidOperationException(message), ILocalizedError
{
    public LocalizedText Detail { get; } = detail;
}
