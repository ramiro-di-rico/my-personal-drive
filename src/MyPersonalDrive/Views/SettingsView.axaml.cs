using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

/// <summary>
/// The settings view: CLI connection, preferences and local directory sync. Markup, plus the two
/// file pickers it needs — a picker belongs to the visual tree, so it stays out of the view model.
/// </summary>
public partial class SettingsView : UserControl
{
    private static Localizer Loc => Localizer.Instance;

    public SettingsView()
    {
        InitializeComponent();
    }

    private async void BrowseCliPath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        // async void: the picker goes through the desktop portal, which can fail, and the assignment
        // below writes settings.json (docs/PLAN-UX-ROUND-4.md Z1).
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T(StringKeys.Picker.CliPathTitle),
                AllowMultiple = false
            });

            if (files.Count == 0)
            {
                return;
            }

            viewModel.CliPath = files[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }

    private async void BrowseDefaultSyncFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        // See BrowseCliPath.
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.T(StringKeys.Picker.DefaultSyncFolderTitle),
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                return;
            }

            viewModel.DefaultSyncFolder = folders[0].Path.LocalPath;
        }
        catch (Exception ex)
        {
            viewModel.ReportHandlerFailure(ex);
        }
    }
}
