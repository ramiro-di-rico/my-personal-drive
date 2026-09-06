using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// What to do about files that already exist at the upload target: overwrite, skip, or cancel.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6), which held seventeen
/// dialogs and about eleven hundred lines of them.
/// </summary>
public static class UploadConflictDialog
{
    private static Localizer Loc => Localizer.Instance;

    public static async Task<UploadConflictStrategy> ShowAsync(Window owner, IReadOnlyList<string> conflictingFiles)
    {
        var filesList = string.Join("\n", conflictingFiles.Take(10).Select(f => "- " + f));
        if (conflictingFiles.Count > 10)
        {
            filesList += Loc.Plural(StringKeys.Common.More, conflictingFiles.Count - 10);
        }

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.UploadConflictTitle),
            Width = 450,
            Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 15,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.UploadConflictIntro), FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = filesList, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = Loc.T(StringKeys.Dialog.UploadConflictQuestion), FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Spacing = 10,
                        Orientation = Avalonia.Layout.Orientation.Vertical,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Children =
                        {
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictKeepBoth), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.KeepBoth },
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictReplace), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Replace },
                            new Button { Content = Loc.T(StringKeys.Dialog.UploadConflictSkip), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.Skip },
                            new Button { Content = Loc.T(StringKeys.Common.Cancel), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Tag = UploadConflictStrategy.None }
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

        await dialog.ShowDialog(owner);
        return result;
    }
}
