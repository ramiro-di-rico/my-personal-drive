using Avalonia.Threading;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The CLI activity console: the log buffer and its filter, the collapsed status line, and the
/// panel's own visibility and height.
///
/// Z5 step 2 (docs/PLAN-UX-ROUND-4.md#z5). It consumes <see cref="ProviderActivity"/> events and
/// produces text; it never touches a provider itself, which is what made it the second cluster to
/// leave the view model. The batching is the reason it is worth having in one place: the CLI's
/// events arrive on whatever thread the process wrote on, so lines are queued under a lock and
/// flushed to the UI thread in one background-priority post rather than one post per line.
/// </summary>
public sealed class ActivityConsoleViewModel : ObservableObject
{
    private readonly CommandLogBuffer _commandLog = new(maxLines: CommandLogBuffer.MaxLines * 2);

    /// <summary>
    /// Guards <see cref="_pendingCommandLines"/>, which the CLI executor's events fill from whatever
    /// thread the process wrote on.
    /// </summary>
    private readonly object _commandLogGate = new();

    private readonly List<string> _pendingCommandLines = new();
    private readonly AppSettingsService _settings;
    private readonly StatusSurface _status;

    private bool _commandLogFlushScheduled;
    private bool _isCommandConsoleVisible = true;
    private int _activeOperationCount;
    private string? _lastLogLine;
    private bool _showOnlyWarningsAndErrors;
    private string _logSearchText = string.Empty;
    private double _commandConsoleMaxHeight = 180;
    private double _commandConsoleHeight = AppSettings.DefaultCommandConsoleHeight;
    private double _commandConsoleOpacity = 1;
    private bool _commandConsoleHitTestVisible = true;
    private string _activeCommand = Localizer.Instance.T(StringKeys.Console.Idle);
    private string _commandLogText = Localizer.Instance.T(StringKeys.Console.NoCommandRunning);
    private string _commandConsoleToggleLabel = Localizer.Instance.T(StringKeys.Console.ToggleHide);
    private string _commandConsoleToggleGlyph = "▼";

    internal ActivityConsoleViewModel(AppSettingsService settings, StatusSurface status, Action<Exception> onError)
    {
        _settings = settings;
        _status = status;

        var appSettings = settings.Load();
        _isCommandConsoleVisible = appSettings.ShowCommandConsole;
        _commandConsoleHeight = appSettings.CommandConsoleHeightOrDefault();
        _commandConsoleMaxHeight = _commandConsoleHeight + ConsoleChromeHeight;
        if (!_isCommandConsoleVisible)
        {
            // Mirrors IsCommandConsoleVisible's setter directly rather than going through it: this
            // runs before the AsyncCommand fields exist, and that setter's command notifications
            // would null-ref against them.
            _commandConsoleMaxHeight = 0;
            _commandConsoleOpacity = 0;
            _commandConsoleHitTestVisible = false;
            _commandConsoleToggleLabel = Localizer.Instance.T(StringKeys.Console.ToggleShow);
            _commandConsoleToggleGlyph = "▲";
        }

        ToggleCommandConsoleCommand = new AsyncCommand(ToggleCommandConsoleAsync, onError: onError);
        ToggleLogFilterCommand = new AsyncCommand(ToggleLogFilterAsync, onError: onError);
        DownloadActivityCommand = new AsyncCommand(DownloadActivityAsync, CanDownloadActivity, onError);
        ClearActivityCommand = new AsyncCommand(ClearActivityAsync, CanClearActivity, onError);
    }

    /// <summary>What the console's own padding and border add on top of the scrolling body.</summary>
    private const double ConsoleChromeHeight = 40;

    /// <summary>Picks a save location for the exported log; null when the view has not wired one.</summary>
    public Func<Task<string?>>? RequestSaveActivityAsync { get; set; }

    public AsyncCommand ToggleCommandConsoleCommand { get; }

    public AsyncCommand ToggleLogFilterCommand { get; }

    public AsyncCommand DownloadActivityCommand { get; }

    public AsyncCommand ClearActivityCommand { get; }

    public int LineCount => _commandLog.Count;

    public string ActiveCommand
    {
        get => _activeCommand;
        private set => SetProperty(ref _activeCommand, value);
    }

    public string CommandLogText
    {
        get => _commandLogText;
        private set => SetProperty(ref _commandLogText, value);
    }

    public string? LastLogLine
    {
        get => _lastLogLine;
        private set => SetProperty(ref _lastLogLine, value);
    }

    public bool ShowOnlyWarningsAndErrors
    {
        get => _showOnlyWarningsAndErrors;
        set
        {
            if (SetProperty(ref _showOnlyWarningsAndErrors, value))
            {
                RefreshCommandLogText();
            }
        }
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
            {
                RefreshCommandLogText();
            }
        }
    }

    public bool IsCommandConsoleVisible
    {
        get => _isCommandConsoleVisible;
        private set
        {
            if (SetProperty(ref _isCommandConsoleVisible, value))
            {
                CommandConsoleMaxHeight = value ? _commandConsoleHeight + ConsoleChromeHeight : 0;
                CommandConsoleOpacity = value ? 1 : 0;
                CommandConsoleHitTestVisible = value;
                CommandConsoleToggleLabel = Loc.T(value ? StringKeys.Console.ToggleHide : StringKeys.Console.ToggleShow);
                CommandConsoleToggleGlyph = value ? "▼" : "▲";
                _settings.Update(s => s.ShowCommandConsole = value);
                DownloadActivityCommand.RaiseCanExecuteChanged();
        ClearActivityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double CommandConsoleMaxHeight
    {
        get => _commandConsoleMaxHeight;
        private set => SetProperty(ref _commandConsoleMaxHeight, value);
    }

    public double CommandConsoleOpacity
    {
        get => _commandConsoleOpacity;
        private set => SetProperty(ref _commandConsoleOpacity, value);
    }

    public bool CommandConsoleHitTestVisible
    {
        get => _commandConsoleHitTestVisible;
        private set => SetProperty(ref _commandConsoleHitTestVisible, value);
    }

    public string CommandConsoleToggleLabel
    {
        get => _commandConsoleToggleLabel;
        private set => SetProperty(ref _commandConsoleToggleLabel, value);
    }

    public string CommandConsoleToggleGlyph
    {
        get => _commandConsoleToggleGlyph;
        private set => SetProperty(ref _commandConsoleToggleGlyph, value);
    }

    /// <summary>
    /// The console body's height, dragged by the handle above it (docs/PLAN-UX-ROUND-3.md X7).
    /// Round 1's Task 4 asked for this and only the collapse toggle shipped; the body has been a
    /// hard-coded 140px ever since. Persisted, so the size the user leaves it at is next launch's.
    /// </summary>
    public double CommandConsoleHeight
    {
        get => _commandConsoleHeight;
        private set
        {
            var clamped = Math.Clamp(value, AppSettings.MinCommandConsoleHeight, AppSettings.MaxCommandConsoleHeight);
            if (SetProperty(ref _commandConsoleHeight, clamped))
            {
                if (IsCommandConsoleVisible)
                {
                    CommandConsoleMaxHeight = clamped + ConsoleChromeHeight;
                }
            }
        }
    }

    /// <summary>
    /// How many CLI/Graph operations are currently mid-flight, across every active provider
    /// session — a real count derived from Started/Finished pairs in <see cref="OnActivity"/>,
    /// unlike <see cref="ActiveCommand"/> (a single label that Task 4's own comment on
    /// <see cref="OnActivity"/> notes can't represent two concurrent operations correctly). Shown
    /// in the floating status line while the console is collapsed.
    /// </summary>
    public int ActiveOperationCount
    {
        get => _activeOperationCount;
        private set
        {
            if (SetProperty(ref _activeOperationCount, value))
            {
                OnPropertyChanged(nameof(ActiveOperationsText));
            }
        }
    }

    public string ActiveOperationsText => Loc.Plural(StringKeys.Console.ActiveOperations, ActiveOperationCount);


    /// <summary>
    /// Buffers a console line and makes sure exactly one flush is pending.
    ///
    /// The old version posted to the UI thread per line and rebuilt the whole console text there,
    /// which re-shaped ~300 KB of text through HarfBuzz on every single line (see
    /// <see cref="CommandLogBuffer"/> for the captured stack). Now the lines accumulate and one
    /// flush drains them, at <see cref="DispatcherPriority.Background"/> so it runs *after* input and
    /// layout — a burst of CLI output can no longer outrun the user's clicks.
    /// </summary>
    /// <summary>A listing the parser could not read strictly; the fallback heuristic ran instead.</summary>
    public void OnListingParseWarning(string accountLabel, string message)
        => Append($"[{accountLabel}] [warn] {message}");

    public void Append(string line)
    {
        lock (_commandLogGate)
        {
            _pendingCommandLines.Add(line);
            if (_commandLogFlushScheduled)
            {
                return;
            }

            _commandLogFlushScheduled = true;
        }

        Dispatcher.UIThread.Post(FlushCommandLog, DispatcherPriority.Background);
    }

    private void FlushCommandLog()
    {
        List<string> batch;
        lock (_commandLogGate)
        {
            _commandLogFlushScheduled = false;
            if (_pendingCommandLines.Count == 0)
            {
                return;
            }

            batch = [.. _pendingCommandLines];
            _pendingCommandLines.Clear();
        }

        var countBefore = _commandLog.Count;
        _commandLog.AddRange(batch);
        LastLogLine = batch[^1];
        RefreshCommandLogText();

        // Only the two activity commands depend on the line count, and only on the empty/non-empty
        // transition. Re-raising all thirteen on every line was pure waste on the UI thread.
        if (countBefore == 0 && _commandLog.Count > 0)
        {
            DownloadActivityCommand.RaiseCanExecuteChanged();
            ClearActivityCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Re-renders <see cref="CommandLogText"/> from the buffer plus whatever the warnings-only
    /// filter and search box currently ask for — called both when new lines arrive and when either
    /// filter input changes, so the two stay in sync without keeping a second copy of the text.
    /// </summary>
    private void RefreshCommandLogText()
    {
        IEnumerable<string> lines = _commandLog.Lines;

        if (_showOnlyWarningsAndErrors)
        {
            lines = lines.Where(line => line.Contains("[warn]", StringComparison.Ordinal)
                || line.Contains("[err]", StringComparison.Ordinal)
                || line.Contains("[fail]", StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(_logSearchText))
        {
            lines = lines.Where(line => line.Contains(_logSearchText, StringComparison.OrdinalIgnoreCase));
        }

        CommandLogText = string.Join(Environment.NewLine, lines);
    }

    private async Task ToggleCommandConsoleAsync()
    {
        IsCommandConsoleVisible = !IsCommandConsoleVisible;
        await Task.CompletedTask;
    }

    private async Task ToggleLogFilterAsync()
    {
        ShowOnlyWarningsAndErrors = !ShowOnlyWarningsAndErrors;
        await Task.CompletedTask;
    }

    private async Task ClearActivityAsync()
    {
        lock (_commandLogGate)
        {
            _pendingCommandLines.Clear();
        }

        _commandLog.Clear();
        CommandLogText = Localizer.Instance.T(StringKeys.Console.NoCommandRunning);
        ActiveCommand = Localizer.Instance.T(StringKeys.Console.Idle);
        LastLogLine = null;
        DownloadActivityCommand.RaiseCanExecuteChanged();
        ClearActivityCommand.RaiseCanExecuteChanged();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Compares the installed CLI against Proton's published Stable release. This is the app's only
    /// outbound network call; everything else goes through the CLI process.
    /// </summary>
    private async Task DownloadActivityAsync()
    {
        var picker = RequestSaveActivityAsync;
        if (picker is null)
        {
            _status.Set(LocalizedText.Of(StringKeys.Status.ActivityUnavailable));
            return;
        }

        var path = await picker();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, CommandLogText);
            _status.Set(LocalizedText.Of(StringKeys.Status.ActivitySaved, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Set(LocalizedText.Of(StringKeys.Status.ActivitySaveFailed, path, ex.Message));
            _status.Warn();
        }
    }

    /// <summary>Re-derives every label the console owns after a language change (docs/PLAN-UX-ROUND-4.md Y7).</summary>
    /// <summary>
    /// Appends lines straight into the log buffer, bypassing the batching, which posts through
    /// <c>Dispatcher.UIThread.Post</c> — a test that went through the real activity pipeline to get
    /// lines into the log would hang without a running Avalonia dispatcher.
    /// </summary>
    internal void AppendCommandLogLinesForTests(IEnumerable<string> lines)
    {
        var list = lines as IReadOnlyList<string> ?? lines.ToList();
        _commandLog.AddRange(list);
        if (list.Count > 0)
        {
            LastLogLine = list[^1];
        }

        RefreshCommandLogText();
    }

    public void OnLanguageChanged()
    {
        CommandConsoleToggleLabel = Localizer.Instance.T(IsCommandConsoleVisible ? StringKeys.Console.ToggleHide : StringKeys.Console.ToggleShow);

        if (_activeOperationCount == 0)
        {
            ActiveCommand = Localizer.Instance.T(StringKeys.Console.Idle);
        }

        // The console shows a placeholder when the buffer is empty and the buffer's own lines
        // otherwise; only the first is translatable.
        if (_commandLog.Lines.Count == 0)
        {
            CommandLogText = Localizer.Instance.T(StringKeys.Console.NoCommandRunning);
        }
        else
        {
            RefreshCommandLogText();
        }

        OnAllPropertiesChanged();
    }

    public void OnActivity(string accountLabel, ProviderActivity activity)
    {
        switch (activity.Kind)
        {
            case ActivityKind.Started:
                Dispatcher.UIThread.Post(() => ActiveCommand = $"[{accountLabel}] {activity.Label}");
                Dispatcher.UIThread.Post(() => ActiveOperationCount++);
                Append($"[{accountLabel}] > {activity.Label}");
                break;

            case ActivityKind.Output:
                Append($"[{accountLabel}] " + (activity.IsError ? $"[err] {activity.Text}" : activity.Text ?? string.Empty));
                break;

            case ActivityKind.Finished:
                Append($"[{accountLabel}] " + (activity.IsError ? $"[fail] exit {activity.ExitCode}" : $"[done] exit {activity.ExitCode}"));
                // Unconditional, same as before P7: with two sessions active, one session's
                // Finished can clear ActiveCommand out from under the other's still-running
                // Started. A single "what's active" label can't represent two concurrent
                // operations correctly — a real per-session indicator is Phase B's job.
                Dispatcher.UIThread.Post(() => ActiveCommand = Localizer.Instance.T(StringKeys.Console.Idle));
                // Clamped rather than trusting Started/Finished to always balance: a session added
                // mid-flight (AddBrowsableAccount) only starts observing from that point on, so its
                // first-ever event could be a Finished with no matching Started counted yet.
                Dispatcher.UIThread.Post(() => ActiveOperationCount = Math.Max(0, ActiveOperationCount - 1));
                break;
        }
    }

    public bool CanDownloadActivity() => _commandLog.Count > 0;

    public bool CanClearActivity() => _commandLog.Count > 0;

    /// <summary>
    /// Writes the dragged height, once, when the drag ends. Not from the setter:
    /// <see cref="AppSettingsService.Update"/> reads settings.json and writes it back, and the
    /// setter runs on every pointer move — a single drag across the console would have been a
    /// hundred read-modify-write cycles on the user's config file
    /// (docs/PLAN-UX-ROUND-4.md Y6).
    /// </summary>
    public void CommitCommandConsoleHeight()
        => _settings.Update(s => s.CommandConsoleHeight = _commandConsoleHeight);

    /// <summary>
    /// Applies one drag step. Dragging the handle up makes the console taller, so the delta is
    /// subtracted — the view passes a raw pointer delta and this owns the direction and the limits.
    /// </summary>
    public void ResizeCommandConsole(double verticalDelta) => CommandConsoleHeight -= verticalDelta;

}
