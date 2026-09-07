using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// Picks the page order and the output name for a PDF merge, for both explorers.
///
/// This is the one dialog the merge feature could not borrow from <see cref="NamePromptDialog"/>:
/// selection in this app is a per-row <c>IsSelected</c> flag, so the order rows were clicked in is
/// not recorded anywhere, and for a merge the order is the whole point. Rather than guess from the
/// grid, the list is shown and reordered explicitly.
/// </summary>
public static class PdfMergeDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> for <paramref name="fileNames"/>, in the
    /// order the caller listed them. Returns null if the user cancelled.
    /// </summary>
    /// <param name="defaultOutputName">The name pre-filled in the box, extension included.</param>
    public static async Task<PdfMergeRequest?> ShowAsync(
        Window owner,
        IReadOnlyList<string> fileNames,
        string defaultOutputName)
    {
        // The working order, as indexes into fileNames. Reordering and removing mutate this; the
        // ListBox is refilled from it, which keeps one source of truth for what the result will be.
        var order = new List<int>(Enumerable.Range(0, fileNames.Count));

        var list = new ListBox
        {
            Height = 190,
            SelectionMode = SelectionMode.Single,
        };

        var moveUp = new Button { Content = "↑", MinWidth = 40 };
        var moveDown = new Button { Content = "↓", MinWidth = 40 };
        var remove = new Button { Content = "✕", MinWidth = 40 };

        // Icon-only buttons say nothing to a screen reader on their own (a11y-theming skill), and
        // an arrow glyph is not a name.
        Avalonia.Automation.AutomationProperties.SetName(moveUp, Loc.T(StringKeys.PdfMerge.MoveUp));
        Avalonia.Automation.AutomationProperties.SetName(moveDown, Loc.T(StringKeys.PdfMerge.MoveDown));
        Avalonia.Automation.AutomationProperties.SetName(remove, Loc.T(StringKeys.PdfMerge.Remove));
        ToolTip.SetTip(moveUp, Loc.T(StringKeys.PdfMerge.MoveUp));
        ToolTip.SetTip(moveDown, Loc.T(StringKeys.PdfMerge.MoveDown));
        ToolTip.SetTip(remove, Loc.T(StringKeys.PdfMerge.Remove));

        var nameBox = new TextBox
        {
            Text = defaultOutputName,
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var summary = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var confirm = new Button { Content = Loc.T(StringKeys.PdfMerge.Confirm), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, MinWidth = 90 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.PdfMerge.Title),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T(StringKeys.PdfMerge.Prompt),
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            list,
                            new StackPanel
                            {
                                [Grid.ColumnProperty] = 1,
                                Spacing = 6,
                                Margin = new Avalonia.Thickness(10, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Children = { moveUp, moveDown, remove },
                            },
                        },
                    },
                    summary,
                    new TextBlock { Text = Loc.T(StringKeys.PdfMerge.OutputNameLabel) },
                    nameBox,
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { confirm, cancel },
                    },
                },
            },
        };

        void Refill(int selectedIndex)
        {
            list.ItemsSource = order.Select((source, position) => $"{position + 1}. {fileNames[source]}").ToList();
            list.SelectedIndex = order.Count == 0
                ? -1
                : Math.Clamp(selectedIndex, 0, order.Count - 1);
            Validate();
        }

        void Validate()
        {
            var name = nameBox.Text?.Trim();
            var nameOk = !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\']) < 0;
            var enough = order.Count >= 2;

            summary.Text = enough
                ? Loc.F(StringKeys.PdfMerge.Summary, order.Count)
                : Loc.T(StringKeys.PdfMerge.NeedTwo);

            confirm.IsEnabled = nameOk && enough;

            var index = list.SelectedIndex;
            moveUp.IsEnabled = index > 0;
            moveDown.IsEnabled = index >= 0 && index < order.Count - 1;
            // Removing is only offered while it would leave a merge behind.
            remove.IsEnabled = index >= 0 && order.Count > 2;
        }

        void Swap(int a, int b)
        {
            (order[a], order[b]) = (order[b], order[a]);
            Refill(b);
        }

        moveUp.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index > 0)
            {
                Swap(index, index - 1);
            }
        };

        moveDown.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index >= 0 && index < order.Count - 1)
            {
                Swap(index, index + 1);
            }
        };

        remove.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (index >= 0 && order.Count > 2)
            {
                order.RemoveAt(index);
                Refill(index);
            }
        };

        list.SelectionChanged += (_, _) => Validate();
        nameBox.TextChanged += (_, _) => Validate();

        dialog.Opened += (_, _) =>
        {
            // Same reasoning as NamePromptDialog: focus the box, and select up to the extension so
            // typing replaces the stem without eating the ".pdf".
            nameBox.Focus();
            var text = nameBox.Text ?? string.Empty;
            var extension = text.LastIndexOf('.');
            nameBox.SelectionStart = 0;
            nameBox.SelectionEnd = extension > 0 ? extension : text.Length;
        };

        PdfMergeRequest? result = null;
        confirm.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim() ?? string.Empty;
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                name += ".pdf";
            }

            result = new PdfMergeRequest([.. order], name);
            dialog.Close();
        };

        cancel.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        Refill(0);
        await dialog.ShowDialog(owner);
        return result;
    }
}
