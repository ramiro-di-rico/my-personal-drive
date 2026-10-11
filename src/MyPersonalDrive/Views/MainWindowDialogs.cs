using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// Hands the view models the dialogs and pickers they ask for. A view model never touches a window
/// or the storage provider; it exposes a <c>Request…Async</c> callback and this class is what
/// answers it, with the main window as owner.
/// </summary>
internal sealed class MainWindowDialogs
{
    private readonly Window _window;

    /// <summary>
    /// The string table. The dialogs are built by hand, so they need the same access the view
    /// models get through <c>ObservableObject.Loc</c>.
    /// </summary>
    private static Localizer Loc => Localizer.Instance;

    public MainWindowDialogs(Window window)
    {
        _window = window;
    }

    public void Attach(MainWindowViewModel viewModel)
    {
        viewModel.RequestUploadFilesAsync = PickUploadFilesAsync;
        viewModel.RequestConflictStrategyAsync = files => Dialogs.UploadConflictDialog.ShowAsync(_window, files);
        viewModel.RequestRenameAsync = PromptForRenameAsync;
        viewModel.RequestCopyNameAsync = PromptForCopyNameAsync;
        viewModel.RequestCreateFolderAsync = PromptForNewFolderNameAsync;
        viewModel.RequestDownloadFolderAsync = PickDownloadFolderAsync;
        viewModel.Console.RequestSaveActivityAsync = PickSaveActivityAsync;
        viewModel.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(_window, question);
        viewModel.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.RequestShowPropertiesAsync = (title, fields, syncStatuses) => Dialogs.PropertiesDialog.ShowAsync(_window, title, fields, syncStatuses);
        viewModel.RequestPdfMergeAsync = (names, suggested) => Dialogs.PdfMergeDialog.ShowAsync(_window, names, suggested);
        viewModel.RequestMoveTargetAsync = (what, startPath) => Dialogs.RemoteFolderPickerDialog.ShowAsync(
            _window,
            viewModel.ListRemoteFolderAsync,
            Loc.F(StringKeys.Dialog.MoveTargetPrompt, what),
            startPath);

        viewModel.LocalExplorer.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(_window, question);
        viewModel.LocalExplorer.RequestRenameAsync = PromptForRenameAsync;
        viewModel.LocalExplorer.RequestCopyToClipboardAsync = CopyToClipboardAsync;
        viewModel.LocalExplorer.RequestShowPropertiesAsync = (title, fields, syncStatuses) => Dialogs.PropertiesDialog.ShowAsync(_window, title, fields, syncStatuses);
        viewModel.LocalExplorer.RequestPdfMergeAsync = (names, suggested) => Dialogs.PdfMergeDialog.ShowAsync(_window, names, suggested);

        viewModel.SyncPanel.RequestNewPairAsync = prefill => Dialogs.SyncPairDialog.ShowAddAsync(_window, viewModel.SyncPanel, viewModel.RootPath, prefill);
        viewModel.SyncPanel.RequestPreviewConfirmationAsync = (plan, warnings) => Dialogs.SyncPreviewDialog.ShowAsync(_window, plan, warnings);
        viewModel.SyncPanel.RequestConflictResolutionsAsync = conflicts => Dialogs.SyncConflictsDialog.ShowAsync(_window, conflicts);
        viewModel.SyncPanel.RequestFailureReviewAsync = failures => Dialogs.SyncFailuresDialog.ShowAsync(_window, failures);
        viewModel.SyncPanel.RequestConfirmationAsync = question => Dialogs.ConfirmDialog.ShowAsync(_window, question);
        viewModel.SyncPanel.RequestEditPairAsync = pair => Dialogs.SyncPairDialog.ShowEditAsync(_window, pair);
        viewModel.SyncPanel.RequestAlertAsync = message => Dialogs.AlertDialog.ShowAsync(_window, message);
    }

    private async Task<IReadOnlyList<string>> PickUploadFilesAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T(StringKeys.Picker.UploadTitle),
            AllowMultiple = true
        });

        return files.Select(file => file.Path.LocalPath).ToList();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T(StringKeys.Picker.DownloadFolderTitle),
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    private Task<string?> PromptForRenameAsync(string currentName)
        => Dialogs.NamePromptDialog.ShowAsync(
            _window,
            Loc.T(StringKeys.Dialog.RenameTitle),
            Loc.F(StringKeys.Dialog.RenamePrompt, currentName),
            Loc.T(StringKeys.Menu.Rename),
            initialText: currentName,
            placeholder: null,
            mustDifferFrom: currentName);

    private Task<string?> PromptForNewFolderNameAsync()
        => Dialogs.NamePromptDialog.ShowAsync(
            _window,
            Loc.T(StringKeys.Dialog.NewFolderTitle),
            Loc.T(StringKeys.Dialog.NewFolderPrompt),
            Loc.T(StringKeys.Common.Create),
            initialText: null,
            placeholder: Loc.T(StringKeys.Dialog.NewFolderPlaceholder));

    private Task<string?> PromptForCopyNameAsync(string currentName)
        => Dialogs.NamePromptDialog.ShowAsync(
            _window,
            Loc.T(StringKeys.Menu.Copy),
            Loc.F(StringKeys.Dialog.CopyPrompt, currentName),
            Loc.T(StringKeys.Common.Copy),
            initialText: null,
            placeholder: Loc.T(StringKeys.Dialog.CopyPlaceholder),
            mustDifferFrom: currentName);

    private async Task<string?> PickSaveActivityAsync()
    {
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(StringKeys.Picker.SaveLogTitle),
            SuggestedFileName = "cli-activity.log",
            DefaultExtension = "log"
        });

        return file?.Path.LocalPath;
    }

    /// <summary>Puts text on the system clipboard — "Copiar ruta" (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6). Silently a no-op if the platform offers no clipboard (e.g. a headless test host).</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = _window.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
