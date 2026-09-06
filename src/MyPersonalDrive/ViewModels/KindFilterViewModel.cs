using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// One chip in the "filter by type" row (docs/PLAN-BROWSER-VIEWS.md M6). Built from the metrics
/// histogram the folder already produced, so the offered filters are exactly the kinds present —
/// a chip that would filter to nothing is never shown.
///
/// <see cref="Kind"/> null is the "All" chip: filtering is a view state, and the way out of it has
/// to be as visible as the way in.
/// </summary>
public sealed class KindFilterViewModel : ObservableObject
{
    private bool _isActive;

    public KindFilterViewModel(FileKind? kind, int count, Func<FileKind?, Task> apply, Action<Exception>? onError = null)
    {
        Kind = kind;
        Count = count;
        ApplyCommand = new AsyncCommand(() => apply(kind), onError: onError);
    }

    public FileKind? Kind { get; }

    public int Count { get; }

    /// <summary>
    /// Read at get time, not stored at construction. Storing it froze the chip row in whichever
    /// language was active when the folder was last listed: switching to Spanish left "All (14)
    /// Folders (8)" on screen until something re-listed the folder (docs/PLAN-UX-ROUND-3.md X8).
    /// </summary>
    public string Label => Kind is null
        ? Localizer.Instance.T(StringKeys.Common.All)
        : FileKindClassifier.DisplayName(Kind.Value);

    public string LabelWithCount => $"{Label} ({Count:n0})";

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public AsyncCommand ApplyCommand { get; }
}
