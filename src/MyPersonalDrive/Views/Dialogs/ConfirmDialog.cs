using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// The blocking yes/no every destructive action goes through. Cancel is the default button.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6), which held seventeen
/// dialogs and about eleven hundred lines of them.
/// </summary>
public static class ConfirmDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// A plain yes/no. "Cancel" is the default button, not "Continue": every question routed here is
    /// a warning about doing something big, so the safe answer should be the one a stray Enter picks.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string question)
    {
        var yes = new Button { Content = Loc.T(StringKeys.Common.Continue), Width = 100 };
        var no = new Button { Content = Loc.T(StringKeys.Common.Cancel), IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.ConfirmTitle),
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

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
