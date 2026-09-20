using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// An item's metadata, with the path copyable because it is the only value here anyone needs
/// elsewhere (docs/PLAN-UX-ROUND-2.md §12).
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6), which held seventeen
/// dialogs and about eleven hundred lines of them.
/// </summary>
public static class PropertiesDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>A read-only "Properties" info panel — "Propiedades" (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 6).</summary>
    /// <param name="syncStatuses">
    /// One row per signed-in provider, rendered as its own titled block below the fields
    /// (docs/PLAN-SYNC-VISIBILITY.md S5). Optional and additive, the way
    /// <see cref="PropertyField.IsCopyable"/> was: every existing call site is unaffected, and a
    /// per-provider breakdown is a section rather than a label/value pair, which is the one thing
    /// this dialog otherwise only knows how to draw.
    /// </param>
    public static async Task ShowAsync(Window owner, string title, IReadOnlyList<PropertyField> fields, IReadOnlyList<SyncStatusRow>? syncStatuses = null)
    {
        var children = new List<Control>
        {
            new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        };

        foreach (var field in fields)
        {
            var text = new TextBlock
            {
                Text = Loc.F(StringKeys.Dialog.PropertiesField, field.Label, field.Value),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (!field.IsCopyable)
            {
                children.Add(text);
                continue;
            }

            // Paths are the only values here anyone needs elsewhere, and the only ones long enough
            // that retyping them is not an option (docs/PLAN-UX-ROUND-2.md §12).
            var copyButton = new Button
            {
                Content = Loc.T(StringKeys.Common.Copy),
                FontSize = 11,
                Padding = new Avalonia.Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var value = field.Value;
            copyButton.Click += async (_, _) =>
            {
                await CopyToClipboardAsync(owner, value);
                // Confirm in place: a clipboard write is otherwise completely invisible.
                copyButton.Content = Loc.T(StringKeys.Common.Copied);
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(copyButton, 1);
            row.Children.Add(text);
            row.Children.Add(copyButton);
            children.Add(row);
        }

        if (syncStatuses is { Count: > 0 })
        {
            // An explicit height: an empty Border with only a top BorderThickness measures zero
            // and draws nothing, which is exactly what the first version of this did — the section
            // ran straight on from the fields with no rule between them.
            children.Add(new Border
            {
                Height = 1,
                Background = owner.TryFindResource("StatusOfflineBrush", owner.ActualThemeVariant, out var rule)
                    ? rule as Avalonia.Media.IBrush
                    : null,
                Opacity = 0.5,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });
            children.Add(new TextBlock
            {
                Text = Loc.T(StringKeys.SyncStatus.SectionTitle),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });

            foreach (var row in syncStatuses)
            {
                var block = new StackPanel { Spacing = 2 };
                block.Children.Add(new TextBlock
                {
                    Text = Loc.F(StringKeys.Dialog.PropertiesField, row.Account, row.StatusText),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });

                // The pair, and when it last ran: a status with no "synced with what, and how long
                // ago" leaves the user to go and find the sync panel to answer it.
                foreach (var detail in new[] { row.Detail, row.LastSyncText })
                {
                    if (string.IsNullOrWhiteSpace(detail))
                    {
                        continue;
                    }

                    block.Children.Add(new TextBlock
                    {
                        Text = detail,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 11,
                        Opacity = 0.75,
                    });
                }

                children.Add(block);
            }
        }

        var okButton = new Button { Content = Loc.T(StringKeys.Common.Ok), IsDefault = true, IsCancel = true, Width = 80 };
        children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { okButton },
        });

        var contentPanel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(20) };
        contentPanel.Children.AddRange(children);

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.PropertiesTitle),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = contentPanel,
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    private static async Task CopyToClipboardAsync(Window owner, string text)
    {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
