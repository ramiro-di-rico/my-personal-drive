using MyPersonalDrive.Models;

namespace MyPersonalDrive.ViewModels.Sync;

/// <summary>
/// One failed queue row, for the failures view (docs/PLAN-UX-ROUND-2.md §6).
///
/// The data was always there — <see cref="QueuedSyncAction"/> carries the path, the operation, the
/// attempt count and the provider's own error text, and
/// <c>SyncStateStore.GetFailedActionsAsync</c> returns the rows in full. The panel just collapsed
/// them to <c>.Count</c> and asked the user to retry blind. This type is the missing display layer,
/// deliberately shaped like the conflict rows the resolve-conflicts dialog already builds.
/// </summary>
public sealed class SyncFailureViewModel
{
    private readonly QueuedSyncAction _action;

    public SyncFailureViewModel(QueuedSyncAction action)
    {
        _action = action;
    }

    public long Id => _action.Id;

    public string RelativePath => _action.RelativePath;

    /// <summary>What the sync was trying to do, in the user's terms rather than the enum's.</summary>
    public string OperationText => _action.Operation switch
    {
        SyncOperation.DownloadFile => "Descargar",
        SyncOperation.UploadFile => "Subir",
        SyncOperation.CreateLocalFolder => "Crear la carpeta local",
        SyncOperation.CreateRemoteFolder => "Crear la carpeta remota",
        SyncOperation.DeleteLocal => "Eliminar el archivo local",
        SyncOperation.TrashRemote => "Mover el archivo remoto a la papelera",
        SyncOperation.RenameLocal => "Renombrar el archivo local",
        SyncOperation.RenameRemote => "Renombrar el archivo remoto",
        SyncOperation.UpdateBaselineOnly => "Actualizar la referencia",
        SyncOperation.ResolveConflictKeepBoth => "Conservar ambas versiones",
        _ => _action.Operation.ToString()
    };

    /// <summary>
    /// The provider's own explanation, verbatim. Not translated and not prettified: this is the
    /// only place the user gets to see why a specific file would not sync, and paraphrasing a
    /// CLI/API message is how you lose the detail that makes it actionable.
    /// </summary>
    public string ReasonText => string.IsNullOrWhiteSpace(_action.LastError)
        ? "El proveedor no informó un motivo."
        : _action.LastError!;

    public string AttemptText => _action.AttemptCount == 1
        ? "1 intento"
        : $"{_action.AttemptCount} intentos";

    /// <summary>Path, operation and attempts on one line, for the dialog's row header.</summary>
    public string Summary => $"{OperationText} · {AttemptText}";
}
