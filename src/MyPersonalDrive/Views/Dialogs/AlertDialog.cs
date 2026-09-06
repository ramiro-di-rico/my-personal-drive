using Avalonia.Controls;
using Avalonia.Layout;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>
/// A blocking, single-button notice — for a rejection the user has to actually see.
/// Extracted from MainWindow's code-behind (docs/PLAN-UX-ROUND-4.md Z6), which held seventeen
/// dialogs and about eleven hundred lines of them.
/// </summary>
public static class AlertDialog
{
    private static Localizer Loc => Localizer.Instance;

    /// <summary>
    /// A blocking, single-button notice — for a rejection the user has to actually see, not a
    /// <c>StatusMessage</c> line that can change again (or scroll away) before anyone reads it.
    /// Mirrors <see cref="AskAsync"/>'s shape with the "Cancel" button dropped: there's nothing to
    /// decide here, only something to acknowledge (docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2 —
    /// a rejected sync-pair-direction change looked indistinguishable from a silently-failed save).
    /// </summary>
    public static async Task ShowAsync(Window owner, string message)
    {
        var ok = new Button { Content = Loc.T(StringKeys.Common.Ok), IsCancel = true, IsDefault = true, Width = 100 };

        var dialog = new Window
        {
            Title = Loc.T(StringKeys.Dialog.AlertTitle),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok }
                    }
                }
            }
        };

        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
    }
}
