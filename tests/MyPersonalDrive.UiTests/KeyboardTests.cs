using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPersonalDrive.Models;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.Views;
using Xunit;

namespace MyPersonalDrive.UiTests;

/// <summary>
/// docs/PLAN-UX-ROUND-4.md Y4. Round 3's X5 shipped nine keyboard gestures and said, honestly, that
/// they were "unverified by hand". This presses the keys against the real window.
///
/// One test, one window, deliberately. Each of these started life as its own <c>[AvaloniaFact]</c>
/// and each passed alone, but a window keeps posting fire-and-forget refreshes to the shared
/// dispatcher after its test ends, and the next test's pump runs them — emptying the listing under
/// it, so every command gated on rows declined and the keyboard looked broken. Closing the window
/// and disabling parallelism both failed to stop it. One window for the whole map does.
///
/// Failures are collected rather than thrown at the first one, so a run reports the whole map
/// instead of the first gesture that broke.
/// </summary>
public class KeyboardTests : WindowLayoutTests
{
    [AvaloniaFact]
    public async Task TheKeyboardMapDoesWhatItClaims()
    {
        var failures = new List<string>();
        var (window, viewModel) = ShowWithRows();

        void Check(string gesture, bool condition, string expected)
        {
            if (!condition)
            {
                failures.Add($"{gesture}: {expected}");
            }
        }

        void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, string.Empty);
            Dispatcher.UIThread.RunJobs();
        }

        // Ctrl+A — in list mode, and then in a tile mode, which is the half X2 could not reach and
        // X5 moved to the window in order to.
        Press(Key.A, RawInputModifiers.Control);
        Check("Ctrl+A", viewModel.SelectedCount == 2, $"selects every row (selected {viewModel.SelectedCount} of {viewModel.RootItems.Count})");

        await viewModel.ShowIconsViewCommand.ExecuteAsync();
        foreach (var node in viewModel.RootItems)
        {
            node.IsSelected = false;
        }

        Press(Key.A, RawInputModifiers.Control);
        Check("Ctrl+A (icons)", viewModel.SelectedCount == 2, $"selects every tile (selected {viewModel.SelectedCount})");
        await viewModel.ShowListViewCommand.ExecuteAsync();

        // F2 renames the one selected row, and does nothing with several.
        string? renaming = null;
        viewModel.RequestRenameAsync = name => { renaming = name; return Task.FromResult<string?>(null); };
        foreach (var node in viewModel.RootItems)
        {
            node.IsSelected = node.DisplayName == "a.txt";
        }

        Press(Key.F2);
        Check("F2", renaming == "a.txt", $"renames the selected row (asked for \"{renaming ?? "nothing"}\")");

        renaming = null;
        foreach (var node in viewModel.RootItems)
        {
            node.IsSelected = true;
        }

        Press(Key.F2);
        Check("F2 (several selected)", renaming is null, $"does nothing (asked for \"{renaming}\")");

        // Delete goes through the command the buttons use, so it inherits their confirmation.
        var confirmAsked = false;
        viewModel.RequestConfirmationAsync = _ => { confirmAsked = true; return Task.FromResult(false); };
        Press(Key.Delete);
        Check("Delete", confirmAsked, "asks before trashing a selection containing a folder");

        // The claim X5's commit message made about Delete, tested rather than repeated: it said the
        // gesture "inherits the confirmation prompt". With a lone file selected it does not — the
        // command only asks when a folder or several items are involved. Trash is recoverable, so
        // this may well be right; it is simply not what the message said.
        confirmAsked = false;
        foreach (var node in viewModel.RootItems)
        {
            node.IsSelected = node.DisplayName == "a.txt";
        }

        Press(Key.Delete);
        Check("Delete (one file)", !confirmAsked, "trashes a single file without asking — recoverable, and worth knowing");

        // X5's other claim — that the row's actions stay reachable because the context menu opens
        // with Shift+F10 — is NOT tested here. The check was written and removed: after switching
        // view modes this harness cannot find a materialized row to send the gesture to, so it
        // reported "row not found" rather than an answer, and a test that cannot fail honestly is
        // worse than an acknowledged gap. Tracked in docs/PLAN-UX-ROUND-4.md Y4.

        // Escape closes the viewer and nothing else.
        typeof(FilePreviewViewModel).GetProperty("IsViewerVisible")!.SetValue(viewModel.Preview, true);
        Press(Key.Escape);
        Check("Escape", !viewModel.Preview.IsViewerVisible, "closes the viewer");

        // Ctrl+F puts focus in the pane's search box.
        Press(Key.F, RawInputModifiers.Control);
        var focused = (TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement() as Control)?.Name;
        Check("Ctrl+F", focused == "CloudSearchBox", $"focuses the search box (focus is on {focused ?? "nothing"})");

        // Ctrl+Shift+N asks for a new folder name.
        var createAsked = false;
        viewModel.RequestCreateFolderAsync = () => { createAsked = true; return Task.FromResult<string?>(null); };
        Press(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
        Check("Ctrl+Shift+N", createAsked, "asks for a new folder name");

        Assert.True(failures.Count == 0, "Gestures that did not do what X5 said they do:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// The rows are seeded through the fake CLI *and* placed directly, so whichever of the window's
    /// startup load, its background refresh, or this call wins the race, the listing holds them.
    /// </summary>
    private (MainWindow Window, MainWindowViewModel ViewModel) ShowWithRows()
    {
        var listing = $"[{FolderJson("u-1", "Photos")}, {FileJson("u-2", "a.txt", 10)}]";
        Executor.RespondForPath("/my-files", listing);
        for (var i = 0; i < 30; i++)
        {
            Executor.EnqueueOutput(listing);
        }

        var (window, viewModel) = Show();

        for (var i = 0; i < 50 && viewModel.IsLoading; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        viewModel.DisplayItems([
            new DriveItem("/my-files/Photos", "Photos", IsFolder: true),
            new DriveItem("/my-files/a.txt", "a.txt", IsFolder: false, Size: 10),
        ]);
        Layout(window);

        Assert.Equal(2, viewModel.RootItems.Count);
        return (window, viewModel);
    }

    private static string FolderJson(string uid, string name)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "user@proton.me" },
              "type": "folder", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;

    private static string FileJson(string uid, string name, long size)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "user@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{size}},
                  "claimedModificationTime": "2026-01-01T00:00:00.000Z",
                  "claimedDigests": { "sha1": "hash-{{uid}}" }
                }
              }
            }
            """;
}
