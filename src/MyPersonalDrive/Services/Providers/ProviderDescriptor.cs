namespace MyPersonalDrive.Services.Providers;

/// <summary>What the settings view's provider picker lists — see <see cref="IProviderCatalog"/>.</summary>
public sealed record ProviderDescriptor(
    ProviderId Id,
    string DisplayName,
    string? AccountIdentity = null,
    bool IsAuthenticated = false)
{
    /// <summary>
    /// What the header dropdown shows under the provider's name. Reaches the string table directly
    /// rather than going through a view model: this record *is* what the picker binds to, and
    /// wrapping it in one more view model to localize two words would be worse. It is presentation,
    /// not the error text §9 of docs/PLAN-I18N.md keeps untranslated.
    /// </summary>
    public string AccountSummary => string.IsNullOrWhiteSpace(AccountIdentity)
        ? Localization.Localizer.Instance.T(IsAuthenticated
            ? Localization.StringKeys.Provider.SignedIn
            : Localization.StringKeys.Provider.SignedOut)
        : AccountIdentity;

    // Identity is Id alone, not the record's default all-property comparison. MainWindowViewModel's
    // AvailableProviders/SelectedProvider getters recompute this list from scratch (brand-new
    // instances) on every access, with AccountIdentity/IsAuthenticated reflecting whatever the live
    // auth state happens to be at that instant. The header ComboBox's two-way SelectedItem binding
    // resolves the bound value against its current ItemsSource via Equals — with full-record
    // equality, two recomputations of "the same" provider whose live fields happened to differ by
    // even one flicker were treated as different items and the selection silently dropped, which is
    // exactly what caused switching directly between two providers via that dropdown to sometimes do
    // nothing (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2) — while going through a third provider
    // first happened to land on a moment where the values lined up.
    public bool Equals(ProviderDescriptor? other) => other is not null && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}
