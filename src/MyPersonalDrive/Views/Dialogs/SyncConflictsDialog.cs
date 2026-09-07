using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// Per-file conflict resolution — the flow U6 later copied for failures.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6).
/// </summary>
public static class SyncConflictsDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// §5.6's per-file resolution panel. Every file starts on "Decide later" rather than a default
    /// action: these are the cases the engine refused to guess at, so the dialog must not guess
    /// either. Closing without choosing anything therefore changes nothing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, ConflictResolution>> ShowAsync(Window owner, IReadOnlyList<QueuedSyncAction> conflicts)
    {
        var chosen = new Dictionary<long, ConflictResolution>();
        var rows = new StackPanel { Spacing = 10 };

        foreach (var conflict in conflicts)
        {
            var selector = new ComboBox
            {
                Width = 260,
                ItemsSource = new[]
                {
                    Loc.T(StringKeys.Dialog.ConflictsChoiceLater),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepBoth),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepLocal),
                    Loc.T(StringKeys.Dialog.ConflictsChoiceKeepRemote),
                },
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

        var applyButton = new Button { Content = Loc.T(StringKeys.Common.Apply), IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.Plural(StringKeys.Dialog.ConflictsTitle, conflicts.Count),
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
                        Text = Loc.T(StringKeys.Dialog.ConflictsIntro),
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

        await dialog.ShowDialog(owner);
        return apply ? chosen : new Dictionary<long, ConflictResolution>();
    }

    private static string DescribeReason(string? reason) => reason switch
    {
        nameof(ConflictReason.BothChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonBothChanged),
        nameof(ConflictReason.BothAppearedDiffering) => Loc.T(StringKeys.Dialog.ConflictsReasonBothAppeared),
        nameof(ConflictReason.RemoteDeletedLocalChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonRemoteDeleted),
        nameof(ConflictReason.LocalDeletedRemoteChanged) => Loc.T(StringKeys.Dialog.ConflictsReasonLocalDeleted),
        _ => Loc.T(StringKeys.Dialog.ConflictsReasonDefault),
    };
}
