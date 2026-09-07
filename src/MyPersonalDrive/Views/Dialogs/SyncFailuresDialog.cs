using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// The per-action detail behind a pair's failure count, with retry and discard
/// (docs/PLAN-UX-ROUND-2.md §6).
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6).
/// </summary>
public static class SyncFailuresDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// The failures view (docs/PLAN-UX-ROUND-2.md §6). Same shape as
    /// <see cref="ShowConflictsAsync"/>, because it is the same kind of decision: here is what
    /// happened per file, choose per file. The pair row previously showed only "N acción(es)
    /// fallaron" and a blind retry-everything button, while the per-action reason sat unread in
    /// the queue.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, SyncFailureDecision>> ShowAsync(Window owner, IReadOnlyList<SyncFailureViewModel> failures)
    {
        var chosen = new Dictionary<long, SyncFailureDecision>();
        var selectors = new List<ComboBox>();
        var rows = new StackPanel { Spacing = 12 };

        foreach (var failure in failures)
        {
            var selector = new ComboBox
            {
                Width = 260,
                ItemsSource = new[]
                {
                    Loc.T(StringKeys.Dialog.FailuresChoiceLeave),
                    Loc.T(StringKeys.Dialog.FailuresChoiceRetry),
                    Loc.T(StringKeys.Dialog.FailuresChoiceDiscard),
                },
                SelectedIndex = 0,
            };

            var id = failure.Id;
            selector.SelectionChanged += (_, _) =>
            {
                switch (selector.SelectedIndex)
                {
                    case 1: chosen[id] = SyncFailureDecision.Retry; break;
                    case 2: chosen[id] = SyncFailureDecision.Discard; break;
                    default: chosen.Remove(id); break;
                }
            };
            selectors.Add(selector);

            rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = failure.RelativePath, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = failure.Summary, Opacity = 0.7, FontSize = 12 },
                    // The provider's own words, verbatim and wrapped rather than trimmed: this is
                    // the sentence that tells the user whether it is their problem or ours.
                    new TextBlock { Text = failure.ReasonText, Opacity = 0.9, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    selector,
                }
            });
        }

        // One choice for every row at once: with dozens of failures, deciding file by file is the
        // exception, not the rule. Picking an option here just drives each row's own selector, so
        // the existing per-row plumbing (and a later manual override) keeps working unchanged.
        var globalSelector = new ComboBox
        {
            Width = 260,
            ItemsSource = new[]
            {
                Loc.T(StringKeys.Dialog.FailuresChoiceLeave),
                Loc.T(StringKeys.Dialog.FailuresChoiceRetry),
                Loc.T(StringKeys.Dialog.FailuresChoiceDiscard),
            },
            SelectedIndex = -1,
        };
        globalSelector.SelectionChanged += (_, _) =>
        {
            if (globalSelector.SelectedIndex < 0)
            {
                return;
            }

            foreach (var selector in selectors)
            {
                selector.SelectedIndex = globalSelector.SelectedIndex;
            }
        };

        var globalRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.T(StringKeys.Dialog.FailuresGlobalLabel), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                globalSelector,
            }
        };

        // Grouped by the provider's own wording, so "44 acciones fallaron" becomes readable at a
        // glance instead of forcing a scroll through every row to spot the pattern.
        var summaryPanel = new StackPanel { Spacing = 2 };
        foreach (var group in failures.GroupBy(f => f.ReasonText).OrderByDescending(g => g.Count()))
        {
            summaryPanel.Children.Add(new TextBlock
            {
                Text = Loc.Plural(StringKeys.Dialog.FailuresSummaryLine, group.Count(), group.Key),
                Opacity = 0.8,
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        var retryAllButton = new Button { Content = Loc.T(StringKeys.Dialog.FailuresRetryAll), Width = 160 };
        var applyButton = new Button { Content = Loc.T(StringKeys.Common.Apply), IsDefault = true, Width = 100 };
        var cancelButton = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.Plural(StringKeys.Dialog.FailuresTitle, failures.Count),
            Width = 620,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T(StringKeys.Dialog.FailuresIntro),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    globalRow,
                    new TextBlock
                    {
                        Text = Loc.T(StringKeys.Dialog.FailuresSummaryTitle),
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        FontSize = 12,
                    },
                    summaryPanel,
                    new ScrollViewer { Height = 220, Content = rows },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { retryAllButton, applyButton, cancelButton }
                    }
                }
            }
        };

        var apply = false;

        // The old one-click behavior, kept: deciding file by file is the new capability, not a new
        // obligation.
        retryAllButton.Click += (_, _) =>
        {
            foreach (var failure in failures)
            {
                chosen[failure.Id] = SyncFailureDecision.Retry;
            }

            apply = true;
            dialog.Close();
        };

        applyButton.Click += (_, _) =>
        {
            apply = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return apply ? chosen : new Dictionary<long, SyncFailureDecision>();
    }
}
