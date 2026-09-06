using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The interface string table, exposed here so that every ViewModel is a valid binding source
    /// for it: the markup writes <c>{Binding Loc[settings.general.title]}</c>, which compiled
    /// bindings resolve statically against the indexer — no reflection, and no per-file
    /// <c>Source=</c> plumbing (docs/PLAN-I18N.md §3).
    ///
    /// Returns <see cref="Localizer.Strings"/> rather than the localizer itself, and that is load
    /// bearing: the façade is a *different object* after a language change, which is what makes a
    /// compiled binding re-read the key. See <see cref="LocalizedStrings"/>.
    /// </summary>
    public LocalizedStrings Loc => Localizer.Instance.Strings;

    /// <summary>
    /// Every property is stale. Used when the interface language changes: a derived label reads
    /// through <see cref="Loc"/> at get time, so the binding only needs telling to re-read, and
    /// naming all of them individually would be several hundred call sites. Avalonia treats an
    /// empty property name as "all properties".
    /// </summary>
    protected void OnAllPropertiesChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>
    /// For properties with no backing field of their own — computed ones whose value depends on
    /// state living elsewhere, which <see cref="SetProperty{T}"/> can't observe.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
