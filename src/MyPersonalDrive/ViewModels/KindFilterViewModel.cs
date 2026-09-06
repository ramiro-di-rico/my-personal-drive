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
        Label = kind is null ? Localizer.Instance.T(StringKeys.Common.All) : FileKindClassifier.DisplayName(kind.Value);
        ApplyCommand = new AsyncCommand(() => apply(kind), onError: onError);
    }

    public FileKind? Kind { get; }

    public int Count { get; }

    public string Label { get; }

    public string LabelWithCount => $"{Label} ({Count:n0})";

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public AsyncCommand ApplyCommand { get; }
}
