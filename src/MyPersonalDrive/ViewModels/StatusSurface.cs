using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The standing status line and everything derived from it: the message, whether it is a warning,
/// the failure behind it, whether the window-level alert strip is up, and whether that strip has a
/// remedy to offer.
///
/// <b>Why this is a type.</b> <c>MainWindowViewModel</c> is 4400 lines across eleven concerns, and
/// the reason it resists being split is not its size — it is that reporting was a private method on
/// it, called 92 times. Anything that can fail has to report, so anything that can fail had to live
/// inside the class. This is step 0 of docs/PLAN-UX-ROUND-4.md Z5: nothing moves out yet, but the
/// thing every future extraction needs to be *given* now exists to be given.
///
/// The view model keeps its public properties as forwarders, so neither the 92 call sites nor the
/// twelve bindings in the markup change. Moving those too would be a hundred-point edit whose only
/// benefit is a longer binding path.
/// </summary>
internal sealed class StatusSurface
{
    private readonly Action _changed;
    private LocalizedText _text;
    private string _message;
    private bool _isWarning;
    private bool _dismissed;

    /// <param name="changed">Raised after any change, so the owner can announce the properties it exposes.</param>
    public StatusSurface(LocalizedText initial, Action changed)
    {
        _text = initial;
        _message = initial.Render();
        _changed = changed;
    }

    public string Message => _message;

    public bool IsWarning => _isWarning;

    /// <summary>The unrendered form, so tests can assert on a key instead of on prose.</summary>
    public LocalizedText Text => _text;

    /// <summary>
    /// The failure behind this message, when a failure is what produced it. Null for a warning the
    /// app raised itself — a refusal, an unsupported preview — because those have no remedy
    /// (docs/PLAN-UX-ROUND-4.md Y3).
    /// </summary>
    public DriveErrorKind? ErrorKind { get; private set; }

    /// <summary>
    /// The window-level strip (docs/PLAN-UX-ROUND-3.md X1). Only warnings get it: routine progress
    /// keeps the status panel, because a banner for "Loaded 14 items" is noise.
    /// </summary>
    public bool IsBannerVisible => _isWarning && !_dismissed && _message.Length > 0;

    /// <summary>The other half of the split: the panel's card carries progress and results only.</summary>
    public bool IsInformational => !_isWarning && _message.Length > 0;

    public bool HasAction => _isWarning && ErrorKind is { } kind && HasRemedy(kind);

    /// <summary>
    /// Which failures the app can offer to do something about. Reconnecting fixes a dead session;
    /// retrying fixes a transport that was momentarily unavailable. Everything else is the provider
    /// refusing this specific request, and repeating it verbatim produces the same refusal
    /// (docs/PLAN-UX-ROUND-4.md Y3).
    /// </summary>
    public static bool HasRemedy(DriveErrorKind kind) => kind is
        DriveErrorKind.NotAuthenticated
        or DriveErrorKind.Network
        or DriveErrorKind.Timeout
        or DriveErrorKind.Busy
        or DriveErrorKind.RateLimited
        or DriveErrorKind.Unknown;

    /// <summary>An ordinary line: progress, a result, a prompt. Clears the warning and its remedy.</summary>
    public void Set(LocalizedText text)
    {
        var rendered = text.Render();
        _text = text;
        if (string.Equals(_message, rendered, StringComparison.Ordinal) && !_isWarning && ErrorKind is null && !_dismissed)
        {
            return;
        }

        _message = rendered;
        // A new message is a new thing to say, so a dismissal of the previous one does not carry
        // over to it (docs/PLAN-UX-ROUND-3.md X1).
        _dismissed = false;
        _isWarning = false;
        ErrorKind = null;
        _changed();
    }

    /// <summary>
    /// A provider operation failed: the message, the warning, and the kind that decides which
    /// remedy the strip offers. One call because the three were three statements at twenty call
    /// sites and the third was simply missing (docs/PLAN-UX-ROUND-4.md Y3).
    /// </summary>
    public void Fail(LocalizedText text, Exception ex)
    {
        Set(text);
        ErrorKind = (ex as DriveException)?.Kind ?? DriveErrorKind.Unknown;
        _isWarning = true;
        _changed();
    }

    /// <summary>A warning this app raised itself: no provider failure, so no remedy.</summary>
    public void Warn()
    {
        if (_isWarning)
        {
            return;
        }

        _isWarning = true;
        _changed();
    }

    public void ClearWarning()
    {
        if (!_isWarning)
        {
            return;
        }

        _isWarning = false;
        _changed();
    }

    /// <summary>Takes the strip down without resolving what it reported.</summary>
    public void Dismiss()
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        _changed();
    }

    /// <summary>
    /// Re-renders the stored form after a language change. Not routed through <see cref="Set"/>,
    /// which clears the warning: a language change must not make a standing warning disappear
    /// (docs/PLAN-I18N.md §6.3).
    /// </summary>
    public void Rerender()
    {
        _message = _text.Render();
        _changed();
    }
}
