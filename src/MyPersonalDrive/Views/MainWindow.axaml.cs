using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels;

namespace MyPersonalDrive.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    private async void BrowseCliPath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select proton-drive executable",
            AllowMultiple = false
        });

        if (files.Count == 0)
        {
            return;
        }

        viewModel.CliPath = files[0].Path.LocalPath;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.RequestUploadFilesAsync = PickUploadFilesAsync;
        viewModel.RequestConflictStrategyAsync = PickConflictStrategyAsync;
        viewModel.RequestRenameAsync = PromptForRenameAsync;
        viewModel.RequestCopyNameAsync = PromptForCopyNameAsync;
        viewModel.RequestDownloadFolderAsync = PickDownloadFolderAsync;
        viewModel.RequestSaveActivityAsync = PickSaveActivityAsync;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch
            {
                // The view-model already surfaced the error in the status panel.
            }
        }
    }

    private async Task<IReadOnlyList<string>> PickUploadFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select files to upload",
            AllowMultiple = true
        });

        return files.Select(file => file.Path.LocalPath).ToList();
    }

    private async Task<string?> PickDownloadFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select download folder",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    private async Task<string?> PromptForRenameAsync(string currentName)
    {
        var textBox = new TextBox
        {
            Text = currentName,
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Rename Item",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Enter new name for '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Rename", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        string? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var renameButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        renameButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> PromptForCopyNameAsync(string currentName)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "Leave empty to use original name",
            Width = 350,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };

        var dialog = new Window
        {
            Title = "Copy Item",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Optional new name for copy of '{currentName}':", FontWeight = Avalonia.Media.FontWeight.Bold },
                    textBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Copy", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        string? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var copyButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        copyButton.Click += (_, _) =>
        {
            result = textBox.Text ?? string.Empty;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<UploadConflictStrategy> PickConflictStrategyAsync(IReadOnlyList<string> conflictingFiles)
    {
        var filesList = string.Join("\n", conflictingFiles.Take(10).Select(f => "- " + f));
        if (conflictingFiles.Count > 10)
        {
            filesList += $"\n... and {conflictingFiles.Count - 10} more";
        }

        var dialog = new Window
        {
            Title = "Upload Conflict",
            Width = 450,
            Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "The following files already exist in the drive:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = filesList, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "What would you like to do?", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Children =
                        {
                            new Button { Content = "Keep Both (Rename new files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.KeepBoth },
                            new Button { Content = "Replace (Overwrite existing files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Replace },
                            new Button { Content = "Skip (Don't upload conflicting files)", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Skip },
                            new Button { Content = "Cancel", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.None }
                        }
                    }
                }
            }
        };

        var result = UploadConflictStrategy.None;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[3];
        foreach (var child in buttonsPanel.Children)
        {
            if (child is Button button)
            {
                button.Click += (_, _) =>
                {
                    result = (UploadConflictStrategy)button.Tag!;
                    dialog.Close();
                };
            }
        }

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> PickSaveActivityAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save CLI activity",
            SuggestedFileName = "cli-activity.log",
            DefaultExtension = "log"
        });

        return file?.Path.LocalPath;
    }
}
