using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// One row in the sync panel. Wraps a <see cref="SyncPair"/> plus the commands to preview,
/// run, and remove it. Kept separate from <see cref="MainWindowViewModel"/> per
/// docs/PLAN-TECH-DEBT.md's recommendation not to keep growing that file.
/// </summary>
public sealed class SyncPairViewModel : ObservableObject
{
    private readonly SyncExecutor _executor;
    private readonly SyncStateStore _stateStore;
    private readonly Action<SyncPairViewModel> _onRemoved;
    private SyncPair _pair;
    private string _statusText = string.Empty;
    private bool _isBusy;

    public SyncPairViewModel(SyncPair pair, SyncExecutor executor, SyncStateStore stateStore, Action<SyncPairViewModel> onRemoved)
    {
        _pair = pair;
        _executor = executor;
        _stateStore = stateStore;
        _onRemoved = onRemoved;

        PreviewCommand = new AsyncCommand(PreviewAsync, () => !IsBusy, ReportError);
        SyncNowCommand = new AsyncCommand(RunAsync, () => !IsBusy, ReportError);
        RemoveCommand = new AsyncCommand(RemoveAsync, () => !IsBusy, ReportError);

        UpdateStatusText();
    }

    public int Id => _pair.Id;

    public string RemotePath => _pair.RemotePath;

    public string LocalPath => _pair.LocalPath;

    public string DirectionText => _pair.Direction switch
    {
        SyncDirection.RemoteToLocal => "Remote → Local",
        SyncDirection.LocalToRemote => "Local → Remote",
        _ => "Two-way",
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PreviewCommand.RaiseCanExecuteChanged();
                SyncNowCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand SyncNowCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    /// <summary>Shown a dry-run plan; returns true if the user chose to run it immediately.</summary>
    public Func<SyncPlan, Task<bool>>? RequestPreviewConfirmationAsync { get; set; }

    public Action<string>? OnError { get; set; }

    private async Task PreviewAsync()
    {
        var confirm = RequestPreviewConfirmationAsync;
        if (confirm is null)
        {
            StatusText = "Preview is not available.";
            return;
        }

        IsBusy = true;
        StatusText = "Scanning...";
        try
        {
            var plan = await _executor.PreviewAsync(_pair);
            UpdateStatusText();
            if (await confirm(plan))
            {
                await RunAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        StatusText = "Syncing...";
        try
        {
            await _executor.RunAsync(_pair);
            _pair = await _stateStore.GetPairAsync(_pair.Id) ?? _pair;
        }
        finally
        {
            IsBusy = false;
            UpdateStatusText();
        }
    }

    private async Task RemoveAsync()
    {
        await _stateStore.DeletePairAsync(_pair.Id);
        _onRemoved(this);
    }

    private void UpdateStatusText()
    {
        StatusText = _pair.LastStatus switch
        {
            SyncPairStatus.Never => "Never synced",
            SyncPairStatus.Ok => $"Up to date ({FormatTime(_pair.LastSyncAt)})",
            SyncPairStatus.PartialFailure => $"Partial failure ({FormatTime(_pair.LastSyncAt)}): {_pair.LastError}",
            SyncPairStatus.Error => $"Error: {_pair.LastError}",
            _ => "Unknown",
        };
    }

    private static string FormatTime(DateTimeOffset? timestamp)
        => timestamp is { } t ? t.ToLocalTime().ToString("g") : "never";

    private void ReportError(Exception ex)
    {
        StatusText = $"Unexpected error: {ex.Message}";
        OnError?.Invoke(ex.Message);
    }
}
