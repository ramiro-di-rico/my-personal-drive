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

    private async Task<NewSyncPairRequest?> PromptForNewPairAsync()
    {
        var remoteBox = new TextBox { PlaceholderText = "/my-files/Documents", Width = 380 };
        var localBox = new TextBox { Width = 280, IsReadOnly = true, PlaceholderText = "Choose a local folder..." };
        var browseButton = new Button { Content = "📂 Browse" };

        // RemoteToLocal stays first, and therefore the default: it's the only direction that
        // cannot destroy anything in the cloud (docs/PLAN-LOCAL-SYNC.md §15).
        var directionBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Download only  (remote → local)",
                "Upload only  (local → remote)",
                "Two-way  (remote ↔ local)",
            },
            SelectedIndex = 0,
        };

        var policyBox = new ComboBox
        {
            Width = 380,
            ItemsSource = new[]
            {
                "Ask me  (park the conflict, decide later)",
                "Keep both  (never loses either version)",
                "Prefer local",
                "Prefer remote",
            },
            SelectedIndex = 0,
        };

        var policyLabel = new TextBlock { Text = "When both sides changed:", FontWeight = Avalonia.Media.FontWeight.Bold };

        // The conflict policy is only ever consulted in two-way mode — a one-way mirror's source
        // side wins by definition, so showing the choice there would imply a decision that
        // doesn't exist.
        void SyncPolicyVisibility()
        {
            var isTwoWay = directionBox.SelectedIndex == 2;
            policyBox.IsVisible = isTwoWay;
            policyLabel.IsVisible = isTwoWay;
        }

        directionBox.SelectionChanged += (_, _) => SyncPolicyVisibility();
        SyncPolicyVisibility();

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

        var addButton = new Button { Content = "Add", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var dialog = new Window
        {
            Title = "Add sync pair",
            Width = 480,
            Height = 420,
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
                    new TextBlock { Text = "Direction:", FontWeight = Avalonia.Media.FontWeight.Bold },
                    directionBox,
                    policyLabel,
                    policyBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { addButton, cancelButton }
                    }
                }
            }
        };

        NewSyncPairRequest? result = null;

        addButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(remoteBox.Text) && !string.IsNullOrWhiteSpace(localBox.Text))
            {
                var direction = directionBox.SelectedIndex switch
                {
                    1 => SyncDirection.LocalToRemote,
                    2 => SyncDirection.TwoWay,
                    _ => SyncDirection.RemoteToLocal,
                };

                var policy = policyBox.SelectedIndex switch
                {
                    1 => ConflictPolicy.KeepBoth,
                    2 => ConflictPolicy.PreferLocal,
                    3 => ConflictPolicy.PreferRemote,
                    _ => ConflictPolicy.Ask,
                };

                result = new NewSyncPairRequest(remoteBox.Text.Trim(), localBox.Text.Trim(), direction, policy);
            }

            dialog.Close();
        };

        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> ShowPreviewAsync(SyncPlan plan)
    {
        var stats = plan.Stats;
        var lines = new List<string>
        {
            $"↓ {stats.FilesToDownload} file(s) to download ({FormatBytes(stats.BytesToDownload)}), {stats.FoldersToCreateLocally} folder(s) to create locally.",
            $"↑ {stats.FilesToUpload} file(s) to upload ({FormatBytes(stats.BytesToUpload)}), {stats.FoldersToCreateRemotely} folder(s) to create remotely.",
            $"🗑 {stats.ToDeleteLocal} local item(s) to local trash, {stats.ToTrashRemote} remote item(s) to Proton's trash.",
        };

        if (stats.Conflicts > 0)
        {
            lines.Add($"⚠ {stats.Conflicts} conflict(s) — both sides changed.");
        }

        var summary = string.Join("\n", lines);

        var actionLines = plan.Actions.Take(50).Select(a => $"{a.Operation}  {a.RelativePath}").ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add($"... and {plan.Actions.Count - 50} more");
        }

        // A plan with no actions but with conflicts isn't "up to date" — under the Ask policy that
        // is precisely the state that needs the user, so don't tell them everything is fine.
        var actionsText = actionLines.Count > 0
            ? string.Join("\n", actionLines)
            : stats.Conflicts > 0
                ? "(no automatic actions — every difference is a conflict awaiting your decision)"
                : "(nothing to do — already up to date)";

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
