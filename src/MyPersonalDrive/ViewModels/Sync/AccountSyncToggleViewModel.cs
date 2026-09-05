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

    public string Label => IsRunning ? $"⏸ {DisplayName}: activada" : $"▶ {DisplayName}: desactivada";

    public AsyncCommand ToggleCommand { get; }

    /// <summary>Called whenever the scheduler's run state might have changed from outside this toggle (e.g. a cycle finishing on its own).</summary>
    public void RaiseState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Label));
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
