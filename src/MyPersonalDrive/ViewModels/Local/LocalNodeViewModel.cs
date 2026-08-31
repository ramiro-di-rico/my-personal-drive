using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

namespace MyPersonalDrive.ViewModels.Local;

/// <summary>
/// One row in the local pane. Deliberately smaller than <see cref="DriveNodeViewModel"/>: the
/// local pane is read-only browsing for now (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3), so there
/// is no download/trash/rename/copy here — those land with the drag-and-drop and context-menu
/// tasks later in the same plan, against local filesystem operations that don't exist yet either.
/// </summary>
public sealed class LocalNodeViewModel : ObservableObject
{
    public LocalNodeViewModel(DriveItem item, Func<DriveItem, Task> navigateAsync, Action<Exception>? onError = null)
    {
        Item = item;
        FileKind = FileKindClassifier.Classify(item.Name, item.IsFolder);
        RowCommand = new AsyncCommand(() => IsFolder ? navigateAsync(Item) : Task.CompletedTask, onError: onError);
    }

    public DriveItem Item { get; }

    public bool IsFolder => Item.IsFolder;

    public string DisplayName => string.IsNullOrWhiteSpace(Item.Name) ? Item.Path : Item.Name;

    public FileKind FileKind { get; }

    public string? SizeText => Item.Size is null ? null : ByteSize.Format(Item.Size.Value);

    public string? ModifiedText => Item.ModifiedAt?.ToLocalTime().ToString("g");

    public AsyncCommand RowCommand { get; }
}
