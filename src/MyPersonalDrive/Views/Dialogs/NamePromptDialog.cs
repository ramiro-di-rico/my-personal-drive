using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// The one prompt behind rename, new folder and copy (docs/PLAN-UX-ROUND-3.md X9). Extracted from
/// MainWindow's code-behind, which held seventeen dialogs and about eleven hundred lines of them —
/// ARCHITECTURE.md §7.5 has said "consider extracting them into their own classes" since before
/// this round (docs/PLAN-UX-ROUND-4.md Z6).
/// </summary>
public static class NamePromptDialog
{
    /// <summary>The string table, reached the way the code-behind reaches it.</summary>
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// The one name prompt behind rename, new folder and copy (docs/PLAN-UX-ROUND-3.md X9). Those
    /// were three copies of the same forty lines differing in four strings, and between them they
    /// carried every defect the item lists: a fixed 180px window around a prompt line that
    /// interpolates a file name and did not wrap, no initial focus, no selection, no validation of
    /// what was typed, and buttons pinned at 80px.
    ///
    /// <paramref name="mustDifferFrom"/> is the current name where there is one: a rename to the
    /// same name and a copy onto its own source are both requests the provider will refuse, and
    /// refusing them here costs a round trip and an error card.
    /// </summary>
    public static async Task<string?> ShowAsync(
        Window owner,
        string title,
        string prompt,
        string confirmLabel,
        string? initialText,
        string? placeholder,
        string? mustDifferFrom = null)
    {
        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder,
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // No fixed width: "Rename" fits 80px and Italian's "Rinomina" is a different length, and
        // there is no reason to pin either.
        var confirm = new Button { Content = confirmLabel, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, MinWidth = 90 };

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            // Grows with the prompt instead of clipping it: the rename prompt carries the file's
            // own name, so its height is not something a constant can know.
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    textBox,
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

        void Validate()
        {
            var value = textBox.Text?.Trim();
            confirm.IsEnabled = !string.IsNullOrEmpty(value)
                && value.IndexOfAny(['/', '\\']) < 0
                && !string.Equals(value, mustDifferFrom, StringComparison.Ordinal);
        }

        textBox.TextChanged += (_, _) => Validate();
        Validate();

        dialog.Opened += (_, _) =>
        {
            // Focus, because nothing was focused before and the user had to click into the box
            // before typing. Selection stops at the last dot: renaming "report.final.pdf" is
            // almost always about the part before the extension, and replacing the extension by
            // accident is the expensive mistake.
            textBox.Focus();
            var text = textBox.Text ?? string.Empty;
            var extension = text.LastIndexOf('.');
            textBox.SelectionStart = 0;
            textBox.SelectionEnd = extension > 0 ? extension : text.Length;
        };

        string? result = null;
        confirm.Click += (_, _) =>
        {
            result = textBox.Text?.Trim();
            dialog.Close();
        };

        cancel.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
