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
        viewModel.RequestConflictResolutionsAsync = ShowConflictsAsync;
        viewModel.RequestConfirmationAsync = AskAsync;
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

    private async Task<bool> ShowPreviewAsync(SyncPlan plan, IReadOnlyList<string> warnings)
    {
        var stats = plan.Stats;
        var lines = new List<string>
        {
            $"↓ {stats.FilesToDownload} file(s) to download ({FormatBytes(stats.BytesToDownload)}), {stats.FoldersToCreateLocally} folder(s) to create locally.",
            $"↑ {stats.FilesToUpload} file(s) to upload ({FormatBytes(stats.BytesToUpload)}), {stats.FoldersToCreateRemotely} folder(s) to create remotely.",
            $"🗑 {stats.ToDeleteLocal} local item(s) to local trash, {stats.ToTrashRemote} remote item(s) to Proton's trash.",
        };

        if (stats.FilesToMoveLocally > 0)
        {
            lines.Add($"↔ {stats.FilesToMoveLocally} file(s) moved to follow Proton Drive — no re-download needed.");
        }

        if (stats.Conflicts > 0)
        {
            lines.Add($"⚠ {stats.Conflicts} conflict(s) — both sides changed.");
        }

        foreach (var warning in warnings)
        {
            lines.Add($"⛔ {warning}");
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

    /// <summary>
    /// §5.6's per-file resolution panel. Every file starts on "Decide later" rather than a default
    /// action: these are the cases the engine refused to guess at, so the dialog must not guess
    /// either. Closing without choosing anything therefore changes nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, ConflictResolution>> ShowConflictsAsync(IReadOnlyList<QueuedSyncAction> conflicts)
    {
        var chosen = new Dictionary<long, ConflictResolution>();
        var rows = new StackPanel { Spacing = 10 };

        foreach (var conflict in conflicts)
        {
            var selector = new ComboBox
            {
                Width = 260,
                ItemsSource = new[] { "Decide later", "Keep both", "Keep my local version", "Keep the Proton Drive version" },
                SelectedIndex = 0,
            };

            var id = conflict.Id;
            selector.SelectionChanged += (_, _) =>
            {
                switch (selector.SelectedIndex)
                {
                    case 1: chosen[id] = ConflictResolution.KeepBoth; break;
                    case 2: chosen[id] = ConflictResolution.KeepLocal; break;
                    case 3: chosen[id] = ConflictResolution.KeepRemote; break;
                    default: chosen.Remove(id); break;
                }
            };

            rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = conflict.RelativePath, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = DescribeReason(conflict.LastError), Opacity = 0.7, FontSize = 12 },
                    selector,
                }
            });
        }

        var applyButton = new Button { Content = "Apply", IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = conflicts.Count == 1 ? "Resolve conflict" : $"Resolve {conflicts.Count} conflicts",
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Both sides of these files changed since the last sync, so neither can win automatically. "
                             + "\"Keep both\" never loses anything: your version is renamed aside and uploaded alongside.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    new ScrollViewer { Height = 280, Content = rows },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { applyButton, cancelButton }
                    }
                }
            }
        };

        var apply = false;
        applyButton.Click += (_, _) =>
        {
            apply = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return apply ? chosen : new Dictionary<long, ConflictResolution>();
    }

    /// <summary>
    /// A plain yes/no. "Cancel" is the default button, not "Continue": every question routed here is
    /// a warning about doing something big, so the safe answer should be the one a stray Enter picks.
    /// </summary>
    private async Task<bool> AskAsync(string question)
    {
        var yes = new Button { Content = "Continue", Width = 100 };
        var no = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = "Are you sure?",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { yes, no }
                    }
                }
            }
        };

        var confirmed = false;
        yes.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return confirmed;
    }

    private static string DescribeReason(string? reason) => reason switch
    {
        nameof(ConflictReason.BothChanged) => "Changed here and on Proton Drive since the last sync.",
        nameof(ConflictReason.BothAppearedDiffering) => "Appeared on both sides with different content, with no shared history.",
        nameof(ConflictReason.RemoteDeletedLocalChanged) => "Deleted on Proton Drive, but changed here.",
        nameof(ConflictReason.LocalDeletedRemoteChanged) => "Deleted here, but changed on Proton Drive.",
        _ => "Conflicting changes on both sides.",
    };

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / 1024.0 / 1024.0:F1} MB"
        };
}
