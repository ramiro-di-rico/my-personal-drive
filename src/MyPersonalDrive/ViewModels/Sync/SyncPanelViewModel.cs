using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// Backs the Sync window: the list of configured pairs plus "add a pair" flow. See
/// docs/PLAN-LOCAL-SYNC.md §12. Only <see cref="SyncDirection.RemoteToLocal"/> pairs can be
/// created so far — that's the only direction <see cref="SyncExecutor"/> implements.
/// </summary>
public sealed class SyncPanelViewModel : ObservableObject
{
    private readonly SyncStateStore _stateStore;
    private readonly SyncExecutor _executor;
    private string _statusMessage = "Add a folder to start syncing it from Proton Drive.";
    private bool _isBusy;

    public SyncPanelViewModel(SyncStateStore stateStore, SyncExecutor executor)
    {
        _stateStore = stateStore;
        _executor = executor;
        Pairs = new ObservableCollection<SyncPairViewModel>();

        AddPairCommand = new AsyncCommand(AddPairAsync, () => !IsBusy, ReportError);
        RefreshCommand = new AsyncCommand(LoadPairsAsync, () => !IsBusy, ReportError);
    }

    public ObservableCollection<SyncPairViewModel> Pairs { get; }

    public AsyncCommand AddPairCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddPairCommand.RaiseCanExecuteChanged();
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Prompts for a new pair's remote/local paths; null means the user canceled.</summary>
    public Func<Task<(string RemotePath, string LocalPath)?>>? RequestNewPairAsync { get; set; }

    /// <summary>Shown a dry-run plan; returns true if the user chose to run it immediately. Forwarded to every row.</summary>
    public Func<SyncPlan, Task<bool>>? RequestPreviewConfirmationAsync { get; set; }

    public async Task InitializeAsync() => await LoadPairsAsync();

    private async Task LoadPairsAsync()
    {
        IsBusy = true;
        try
        {
            var pairs = await _stateStore.GetPairsAsync();
            Pairs.Clear();
            foreach (var pair in pairs)
            {
                AddPairViewModel(pair);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddPairViewModel(SyncPair pair)
    {
        var viewModel = new SyncPairViewModel(pair, _executor, _stateStore, RemovePairViewModel)
        {
            RequestPreviewConfirmationAsync = RequestPreviewConfirmationAsync,
            OnError = message => StatusMessage = message,
        };
        Pairs.Add(viewModel);
    }

    private void RemovePairViewModel(SyncPairViewModel viewModel) => Pairs.Remove(viewModel);

    private async Task AddPairAsync()
    {
        var requester = RequestNewPairAsync;
        if (requester is null)
        {
            StatusMessage = "Adding a sync pair is not available.";
            return;
        }

        var input = await requester();
        if (input is null)
        {
            return;
        }

        var (remotePath, localPath) = input.Value;
        var validationError = Validate(remotePath, localPath);
        if (validationError is not null)
        {
            StatusMessage = validationError;
            return;
        }

        IsBusy = true;
        try
        {
            var pair = await _stateStore.CreatePairAsync(remotePath, localPath, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
            AddPairViewModel(pair);
            StatusMessage = $"Added: {pair.RemotePath} → {pair.LocalPath}";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (the pair's UNIQUE(RemotePath, LocalPath))
        {
            StatusMessage = "That remote/local combination is already a sync pair.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cheap, high-value checks only (see docs/PLAN-LOCAL-SYNC.md §12's full list for what's
    /// deliberately not implemented yet: nested-pair detection, free-space estimation).
    /// </summary>
    private static string? Validate(string remotePath, string localPath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
        {
            return "The remote path must be an absolute path starting with '/'.";
        }

        if (string.IsNullOrWhiteSpace(localPath))
        {
            return "Choose a local folder.";
        }

        var trimmed = localPath.TrimEnd('/', '\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmed == "/" || trimmed == home)
        {
            return "Refusing to sync your entire home directory or the filesystem root — pick a specific subfolder.";
        }

        return null;
    }

    private void ReportError(Exception ex) => StatusMessage = $"Unexpected error: {ex.Message}";
}
