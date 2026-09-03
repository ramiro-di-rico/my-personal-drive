using MyPersonalDrive.Models;

namespace MyPersonalDrive.ViewModels;

/// <summary>One row in <see cref="TransferQueueViewModel"/> — a single drag-and-drop transfer's status. See docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5.</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts;
    private TransferStatus _status = TransferStatus.Queued;
    private string? _errorMessage;

    public TransferItemViewModel(TransferDirection direction, string sourceLabel, string targetLabel, CancellationTokenSource cts)
    {
        Direction = direction;
        SourceLabel = sourceLabel;
        TargetLabel = targetLabel;
        _cts = cts;
        CancelCommand = new AsyncCommand(CancelAsync, () => Status is TransferStatus.Queued or TransferStatus.Transferring);
    }

    public TransferDirection Direction { get; }

    public string SourceLabel { get; }

    public string TargetLabel { get; }

    /// <summary>Set only by <see cref="TransferQueueViewModel"/>'s runner and by this item's own <see cref="CancelCommand"/> — never bound to as a settable UI property.</summary>
    public TransferStatus Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText => Status switch
    {
        TransferStatus.Queued => "En cola",
        TransferStatus.Transferring => Direction == TransferDirection.Upload ? "Subiendo…" : "Descargando…",
        TransferStatus.Done => "Listo",
        TransferStatus.Failed => "Error",
        TransferStatus.Cancelled => "Cancelado",
        _ => Status.ToString()
    };

    public string? ErrorMessage
    {
        get => _errorMessage;
        internal set => SetProperty(ref _errorMessage, value);
    }

    public AsyncCommand CancelCommand { get; }

    private Task CancelAsync()
    {
        _cts.Cancel();
        // Immediate feedback for a still-queued item: the queue runner won't get to it — and
        // therefore won't observe the cancellation and update Status itself — until every item
        // ahead of it finishes. A Transferring item's Status instead flips once the
        // OperationCanceledException the token produces actually unwinds out of the running call.
        if (Status == TransferStatus.Queued)
        {
            Status = TransferStatus.Cancelled;
        }

        return Task.CompletedTask;
    }
}
