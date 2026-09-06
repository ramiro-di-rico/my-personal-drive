using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// What a run would do, before it does it: the plan and any warnings, with a confirm.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6).
/// </summary>
public static class SyncPreviewDialog
{
    private static Localizer Loc => Localizer.Instance;

    public static async Task<bool> ShowAsync(Window owner, SyncPlan plan, IReadOnlyList<string> warnings)
    {
        var stats = plan.Stats;
        // Each count is its own clause, joined here. The three summary lines used to be single
        // strings with "archivo(s)"/"carpeta(s)" spliced in — a Spanish-specific plural hack, and
        // one that cannot agree correctly anyway when a sentence carries two different counts
        // (docs/PLAN-I18N.md §6.3). Two clauses, two plural lookups, one line on screen.
        static string TwoClauses(string first, string second) => first + ", " + second;

        var lines = new List<string>
        {
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewDownloadFiles, stats.FilesToDownload, ByteSize.Format(stats.BytesToDownload)),
                Loc.Plural(StringKeys.Dialog.PreviewDownloadFolders, stats.FoldersToCreateLocally)),
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewUploadFiles, stats.FilesToUpload, ByteSize.Format(stats.BytesToUpload)),
                Loc.Plural(StringKeys.Dialog.PreviewUploadFolders, stats.FoldersToCreateRemotely)),
            TwoClauses(
                Loc.Plural(StringKeys.Dialog.PreviewTrashLocal, stats.ToDeleteLocal),
                Loc.Plural(StringKeys.Dialog.PreviewTrashRemote, stats.ToTrashRemote)),
        };

        if (stats.FilesToMoveLocally > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewMovedLocally, stats.FilesToMoveLocally));
        }

        if (stats.FilesToMoveRemotely > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewMovedRemotely, stats.FilesToMoveRemotely));
        }

        if (stats.Conflicts > 0)
        {
            lines.Add(Loc.Plural(StringKeys.Dialog.PreviewConflicts, stats.Conflicts));
        }

        foreach (var warning in warnings)
        {
            lines.Add(Loc.F(StringKeys.Dialog.PreviewWarning, warning));
        }

        var summary = string.Join("\n", lines);

        var actionLines = plan.Actions.Take(50).Select(a => Loc.F(StringKeys.Dialog.PreviewAction, a.Operation, a.RelativePath)).ToList();
        if (plan.Actions.Count > 50)
        {
            actionLines.Add(Loc.Plural(StringKeys.Common.More, plan.Actions.Count - 50).TrimStart('\n'));
        }

        // A plan with no actions but with conflicts isn't "up to date" — under the Ask policy that
        // is precisely the state that needs the user, so don't tell them everything is fine.
        var actionsText = actionLines.Count > 0
            ? string.Join("\n", actionLines)
            : stats.Conflicts > 0
                ? Loc.T(StringKeys.Dialog.PreviewNoActionsConflicts)
                : Loc.T(StringKeys.Dialog.PreviewNoActionsUpToDate);

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PreviewTitle),
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
                            new Button { Content = Loc.T(StringKeys.Menu.SyncNow), IsDefault = true, Width = 160, IsEnabled = plan.Actions.Count > 0 },
                            new Button { Content = Loc.T(StringKeys.Common.Close), IsCancel = true, Width = 80 }
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

        await dialog.ShowDialog(owner);
        return result;
    }
}
