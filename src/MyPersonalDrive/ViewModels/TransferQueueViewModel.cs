using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The drag-and-drop transfer queue (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5) — composed into
/// <see cref="MainWindowViewModel"/> the same way <c>SyncPanel</c>/<c>LocalExplorer</c> already are.
///
/// Processes one transfer at a time, matching the existing single-<c>IsLoading</c> assumption for
/// mutating operations elsewhere in this VM (only read-only listing commands run concurrently) —
/// not <c>Task.WhenAll</c>, so a burst of dropped items doesn't spawn a CLI process per item at once.
///
/// An upload batches every dragged local path into one <c>IDriveOperations.UploadFilesAsync</c>
/// call (the CLI command itself takes a path list), so a multi-file drop is one queue item. A
/// download has no such batch API — <c>DownloadFileAsync</c> takes one remote path per call — so
/// each dragged cloud item becomes its own queue entry. This asymmetry mirrors what the underlying
/// operations actually support; it isn't an arbitrary inconsistency.
/// </summary>
public sealed class TransferQueueViewModel : ObservableObject
{
    private readonly Queue<PendingTransfer> _pending = new();
    private readonly object _gate = new();
    private bool _isProcessing;

    public TransferQueueViewModel()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    public ObservableCollection<TransferItemViewModel> Items { get; } = new();

    /// <summary>Whether the queue panel has anything to show — a real bool, since Avalonia's compiled bindings don't refresh a bound `Items.Count` on collection changes.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>"2 transfiriendo · 3 en cola", or a quiet default when nothing is happening.</summary>
    public string Summary
    {
        get
        {
            var transferring = Items.Count(i => i.Status == TransferStatus.Transferring);
            var queued = Items.Count(i => i.Status == TransferStatus.Queued);

            if (transferring == 0 && queued == 0)
            {
                return Loc.T(StringKeys.Transfer.None);
            }

            var parts = new List<string>();
            if (transferring > 0)
            {
                parts.Add(Loc.F(StringKeys.Transfer.Transferring, transferring));
            }

            if (queued > 0)
            {
                parts.Add(Loc.F(StringKeys.Transfer.QueuedCount, queued));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Returns once *this* item settles (Done/Failed/Cancelled) — not once the whole queue drains —
    /// with the settled item itself, so a caller can tell success from failure (e.g. to word a
    /// confirmation message honestly instead of always claiming success). Callers that only care
    /// that it's queued (most drop handlers) can discard the task.
    /// </summary>
    public Task<TransferItemViewModel> EnqueueUpload(IDriveOperations operations, IReadOnlyList<string> localPaths, string targetPath, UploadConflictStrategy strategy)
        => Enqueue(TransferDirection.Upload, DescribeBatch(localPaths), targetPath,
            ct => operations.UploadFilesAsync(localPaths, targetPath, strategy, ct));

    public Task<TransferItemViewModel> EnqueueDownload(IDriveOperations operations, DriveItem item, string targetPath)
        => Enqueue(TransferDirection.Download, item.Name, targetPath,
            ct => operations.DownloadFileAsync(item.Path, targetPath, ct));

    private Task<TransferItemViewModel> Enqueue(TransferDirection direction, string sourceLabel, string targetLabel, Func<CancellationToken, Task> run)
    {
        var cts = new CancellationTokenSource();
        var item = new TransferItemViewModel(direction, sourceLabel, targetLabel, cts);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferItemViewModel.Status))
            {
                OnPropertyChanged(nameof(Summary));
            }
        };

        Items.Add(item);
        OnPropertyChanged(nameof(Summary));

        var completion = new TaskCompletionSource<TransferItemViewModel>();
        lock (_gate)
        {
            _pending.Enqueue(new PendingTransfer(item, run, cts, completion));
        }

        _ = ProcessQueueAsync();
        return completion.Task;
    }

    /// <summary>
    /// The single worker loop. <see cref="_isProcessing"/> guards against a second overlapping
    /// loop starting from a concurrent <see cref="Enqueue"/> call — only one drains the queue at a
    /// time, and it keeps draining until empty rather than one item per invocation.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        lock (_gate)
        {
            if (_isProcessing)
            {
                return;
            }

            _isProcessing = true;
        }

        while (true)
        {
            PendingTransfer? next;
            lock (_gate)
            {
                next = _pending.Count > 0 ? _pending.Dequeue() : null;
                if (next is null)
                {
                    _isProcessing = false;
                    break;
                }
            }

            var (item, run, cts, completion) = next;
            if (cts.IsCancellationRequested)
            {
                item.Status = TransferStatus.Cancelled;
                completion.SetResult(item);
                continue;
            }

            item.Status = TransferStatus.Transferring;
            try
            {
                await run(cts.Token);
                item.Status = TransferStatus.Done;
            }
            catch (OperationCanceledException)
            {
                item.Status = TransferStatus.Cancelled;
            }
            catch (InvalidOperationException ex)
            {
                item.Status = TransferStatus.Failed;
                item.ErrorMessage = ex.Message;
            }

            completion.SetResult(item);
        }
    }

    private static string DescribeBatch(IReadOnlyList<string> localPaths)
        => localPaths.Count == 1
            ? Path.GetFileName(localPaths[0])
            : $"{localPaths.Count} elementos";

    private sealed record PendingTransfer(TransferItemViewModel Item, Func<CancellationToken, Task> Run, CancellationTokenSource Cts, TaskCompletionSource<TransferItemViewModel> Completion);
}
