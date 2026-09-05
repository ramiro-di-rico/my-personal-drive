using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// One account's "automatic sync on/off" control — the per-account counterpart to
/// <see cref="SyncPanelViewModel"/>'s own (primary-only, kept for backward compatibility)
/// <c>ToggleAutomaticSyncCommand</c>. P7 Phase A: with Proton and OneDrive both possibly syncing at
/// once, pausing one must not touch the other, so each gets its own independent toggle rather than
/// one shared on/off switch (docs/PLAN-CLOUD-PROVIDERS.md P7).
///
/// Deliberately self-contained rather than routed through <see cref="SyncPanelViewModel"/>: the
/// panel's own primary-slot toggle keeps its original logic untouched for the many existing
/// single-account tests, and this is a small enough duplicate that decoupling it beats threading a
/// shared method through two call surfaces with different lifetimes.
/// </summary>
public sealed class AccountSyncToggleViewModel : ObservableObject
{
    private readonly SyncScheduler? _scheduler;
    private readonly SyncStateStore _stateStore;

    public AccountSyncToggleViewModel(string displayName, SyncScheduler? scheduler, SyncStateStore stateStore)
    {
        DisplayName = displayName;
        _scheduler = scheduler;
        _stateStore = stateStore;
        ToggleCommand = new AsyncCommand(ToggleAsync, () => _scheduler is not null);
    }

    public string DisplayName { get; }

    public bool IsRunning => _scheduler?.IsRunning ?? false;

    /// <summary>
    /// Whether this account's toggle is worth showing at all. A provider with no session has a
    /// scheduler loop running like every other, but that loop skips every cycle, so reporting it
    /// as "activada" told the user something that was true of the loop and false of the app
    /// (docs/PLAN-UX-ROUND-2.md §11).
    /// </summary>
    public bool IsRelevant => _scheduler?.IsAccountAuthenticated ?? false;

    /// <summary>
    /// The account's name on its own. The state lives in <see cref="StateText"/> and the verb in
    /// <see cref="ActionTooltip"/>: this used to be one string mixing all three ("⏸ Proton Drive:
    /// activada" showed the action glyph next to the current state, which reads as a
    /// contradiction), and it sat next to the filter chips looking exactly like one of them
    /// (docs/PLAN-UX-ROUND-2.md §7).
    /// </summary>
    public string Label => DisplayName;

    public string StateText => IsRunning ? "activada" : "pausada";

    public string ActionTooltip => IsRunning
        ? $"Pausar la sincronización automática de {DisplayName}"
        : $"Activar la sincronización automática de {DisplayName}";

    public AsyncCommand ToggleCommand { get; }

    /// <summary>Called whenever the scheduler's run state might have changed from outside this toggle (e.g. a cycle finishing on its own).</summary>
    public void RaiseState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsRelevant));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ActionTooltip));
    }

    private async Task ToggleAsync()
    {
        if (_scheduler is null)
        {
            return;
        }

        if (_scheduler.IsRunning)
        {
            await _scheduler.StopAsync();
            await _stateStore.SetAutomaticSyncEnabledAsync(false);
        }
        else
        {
            _scheduler.Start();
            await _stateStore.SetAutomaticSyncEnabledAsync(true);
        }

        RaiseState();
    }
}
