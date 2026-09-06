using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// One chip in the Sync window's "filter by account" row (docs/PLAN-CLOUD-PROVIDERS.md P9) — the
/// same shape as <c>ViewModels.KindFilterViewModel</c>, the folder browser's own "filter by type"
/// chips, reused here rather than inventing a second pattern for the same idea.
///
/// <see cref="AccountLabel"/> null is the "All" chip: filtering is a view state, and the way out
/// of it has to be as visible as the way in.
/// </summary>
public sealed class ProviderFilterViewModel : ObservableObject
{
    private bool _isActive;

    public ProviderFilterViewModel(string? accountLabel, int count, Func<string?, Task> apply, Action<Exception>? onError = null)
    {
        AccountLabel = accountLabel;
        Count = count;
        ApplyCommand = new AsyncCommand(() => apply(accountLabel), onError: onError);
    }

    public string? AccountLabel { get; }

    public int Count { get; }

    /// <summary>Read at get time — see <see cref="ViewModels.KindFilterViewModel.Label"/>.</summary>
    public string Label => AccountLabel ?? Localizer.Instance.T(StringKeys.Common.All);

    public string LabelWithCount => Loc.F(StringKeys.Sync.FilterLabel, Label, Count.ToString("n0", Loc.Culture));

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public AsyncCommand ApplyCommand { get; }
}
