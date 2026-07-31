using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views;

public partial class SyncWindow : Window
{
    public SyncWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SyncPanelViewModel viewModel)
        {
            return;
        }

        viewModel.RequestNewPairAsync = PromptForNewPairAsync;
        viewModel.RequestPreviewConfirmationAsync = ShowPreviewAsync;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is SyncPanelViewModel viewModel)
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch
            {
                // The view-model already surfaced the error via StatusMessage.
            }
        }
    }

    private async Task<(string RemotePath, string LocalPath)?> PromptForNewPairAsync()
    {
        var remoteBox = new TextBox { PlaceholderText = "/my-files/Documents", Width = 380 };
        var localBox = new TextBox { Width = 280, IsReadOnly = true, PlaceholderText = "Choose a local folder..." };
        var browseButton = new Button { Content = "📂 Browse" };

        browseButton.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the local folder to sync into",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                localBox.Text = folders[0].Path.LocalPath;
            }
        };

        var dialog = new Window
        {
            Title = "Add sync pair",
            Width = 480,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Remote folder path:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    remoteBox,
                    new TextBlock { Text = "Local folder:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { localBox, browseButton }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Add", IsDefault = true, Width = 80 },
                            new Button { Content = "Cancel", IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        (string RemotePath, string LocalPath)? result = null;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[4];
        var addButton = (Button)buttonsPanel.Children[0];
        var cancelButton = (Button)buttonsPanel.Children[1];

        addButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(remoteBox.Text) && !string.IsNullOrWhiteSpace(localBox.Text))
            {
                result = (remoteBox.Text.Trim(), localBox.Text.Trim());
            }

            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> ShowPreviewAsync(SyncPlan plan)
    {
        var summary = $"{plan.Stats.FilesToDownload} file(s) to download ({FormatBytes(plan.Stats.BytesToDownload)}), " +
                      $"{plan.Stats.FoldersToCreateLocally} folder(s) to create, " +
                      $"{plan.Stats.ToDeleteLocal} local item(s) to move to trash.";

        var actionLines = plan.Actions.Take(50).Select(a => $"{a.Operation}  {a.RelativePath}").ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add($"... and {plan.Actions.Count - 50} more");
        }

        var actionsText = actionLines.Count == 0 ? "(nothing to do — already up to date)" : string.Join("\n", actionLines);

        var dialog = new Window
        {
            Title = "Sync preview",
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = summary, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new ScrollViewer
                    {
                        Height = 230,
                        Content = new TextBlock { Text = actionsText, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new Button { Content = "Run now", IsDefault = true, Width = 100, IsEnabled = plan.Actions.Count > 0 },
                            new Button { Content = "Close", IsCancel = true, Width = 80 }
                        }
                    }
                }
            }
        };

        var result = false;
        var panel = (StackPanel)dialog.Content;
        var buttonsPanel = (StackPanel)panel.Children[2];
        var runButton = (Button)buttonsPanel.Children[0];
        var closeButton = (Button)buttonsPanel.Children[1];

        runButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        closeButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };
}
