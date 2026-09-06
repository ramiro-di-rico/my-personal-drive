using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.ViewModels.Sync;

namespace MyPersonalDrive.ViewModels.Local;

/// <summary>
/// The local pane of the dual-pane explorer (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 3) — the
/// filesystem-facing counterpart to the cloud pane already on <c>MainWindowViewModel</c>. Composed
/// into it the same way <c>FolderMetricsViewModel</c>/<c>SyncPanelViewModel</c> already are, rather
/// than folded into the parent VM directly: this is its own state machine (current path, listing,
/// hidden-files preference) with no cloud dependency.
/// </summary>
public sealed class LocalExplorerViewModel : ObservableObject
{
    private readonly LocalFileSystemService _service;
    private readonly AppSettingsService _settings;
    private readonly Action<Exception>? _onError;
    private string _currentPath;
    private bool _showHiddenFiles;
    private string _freeSpaceText = string.Empty;
    private bool _isLoading;
    private string? _statusMessage;
    private LocalizedText _statusText = LocalizedText.None;
    private string _searchText = string.Empty;
    private bool _hasRenderedListing;
    private string? _selectionAnchorPath;

    /// <summary>Everything the current folder holds, before filtering — mirrors <c>MainWindowViewModel._loadedItems</c>, and for the same reason: <see cref="Items"/> is a filtered view of this, never the source of truth for what's actually in the folder.</summary>
    private IReadOnlyList<DriveItem> _loadedItems = [];

    public LocalExplorerViewModel(LocalFileSystemService service, AppSettingsService settings, Action<Exception>? onError = null)
    {
        _service = service;
        _settings = settings;
        _onError = onError;
        HomePath = service.GetHomeDirectory();
        _currentPath = HomePath;
        _showHiddenFiles = settings.Load().ShowHiddenLocalFiles;
        Items = new ObservableCollection<LocalNodeViewModel>();
        BreadcrumbItems = new ObservableCollection<BreadcrumbSegmentViewModel>();

        RefreshCommand = new AsyncCommand(() => NavigateAsync(_currentPath), () => !IsLoading, onError);
        GoHomeCommand = new AsyncCommand(() => NavigateAsync(HomePath), () => !IsLoading, onError);
        BackCommand = new AsyncCommand(GoBackAsync, () => !IsLoading && CanGoBack, onError);
        ToggleHiddenFilesCommand = new AsyncCommand(ToggleHiddenFilesAsync, () => !IsLoading, onError);
        SelectAllCommand = new AsyncCommand(SelectAllAsync, () => Items.Count > 0, onError);
        DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync, () => SelectedCount > 0, onError);
        ClearSearchCommand = new AsyncCommand(ClearSearchAsync, () => HasSearchText, onError);

        // Long-lived, like the window itself, so subscribing without unsubscribing is not a leak.
        // Deliberately not done in ObservableObject: a row view model is recreated on every
        // listing, and the singleton would accumulate a handler per row (docs/PLAN-I18N.md §3).
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            _statusMessage = _statusText.IsEmpty ? null : _statusText.Render();
            OnAllPropertiesChanged();

            // The rows are their own binding sources; the notification above does not reach them.
            foreach (var row in Items)
            {
                row.RefreshLocalizedText();
            }
        };
    }

    public ObservableCollection<LocalNodeViewModel> Items { get; }

    public ObservableCollection<BreadcrumbSegmentViewModel> BreadcrumbItems { get; }

    public string HomePath { get; }

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    private bool CanGoBack => !PathsEqual(CurrentPath, System.IO.Path.GetPathRoot(CurrentPath) ?? CurrentPath);

    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        private set => SetProperty(ref _showHiddenFiles, value);
    }

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set
        {
            if (SetProperty(ref _freeSpaceText, value))
            {
                OnPropertyChanged(nameof(FreeSpaceLabel));
            }
        }
    }

    /// <summary>
    /// The free-space line as shown. The markup used to wrap <see cref="FreeSpaceText"/> in a
    /// <c>StringFormat</c> of "{0} free" — which had survived the round that made the interface
    /// Spanish (PLAN-UX-ROUND-2 U4) precisely because a format string inside a binding does not
    /// look like a literal.
    /// </summary>
    public string FreeSpaceLabel => Loc.F(StringKeys.Local.FreeSpace, FreeSpaceText);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseEmptyStateChanged();
            }
        }
    }

    /// <summary>
    /// The local pane's status line. Same deferred shape as the cloud pane's
    /// (<c>MainWindowViewModel.StatusMessage</c>): the stored form is the key, so the line follows
    /// the language picker instead of being frozen (docs/PLAN-I18N.md §6.3).
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusText = LocalizedText.Verbatim(value);
            SetProperty(ref _statusMessage, value);
        }
    }

    /// <summary>The unrendered form, so tests can assert on a key instead of on prose.</summary>
    internal LocalizedText StatusText => _statusText;

    private void SetStatus(LocalizedText text)
    {
        _statusText = text;
        var rendered = text.IsEmpty ? null : text.Render();
        SetProperty(ref _statusMessage, rendered, nameof(StatusMessage));
    }

    private void SetStatus(string key, params object?[] args) => SetStatus(LocalizedText.Of(key, args));

    private void SetStatusPlural(string keyPrefix, int count, params object?[] args)
        => SetStatus(LocalizedText.Plural(keyPrefix, count, args));

    public AsyncCommand RefreshCommand { get; }

    public AsyncCommand GoHomeCommand { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand ToggleHiddenFilesCommand { get; }

    public AsyncCommand SelectAllCommand { get; }

    public AsyncCommand DeleteSelectedCommand { get; }

    /// <summary>Empties this pane's search box (docs/PLAN-UX-ROUND-2.md §9).</summary>
    public AsyncCommand ClearSearchCommand { get; }

    /// <summary>How many rows are currently selected — docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2.</summary>
    public int SelectedCount => Items.Count(i => i.IsSelected);

    public bool HasMultipleSelected => SelectedCount > 1;

    public string SelectionSummaryText => SelectedCount switch
    {
        0 => string.Empty,
        1 => "1 elemento seleccionado",
        _ => $"{SelectedCount} elementos seleccionados",
    };

    /// <summary>A yes/no confirmation, used before permanently deleting a local item.</summary>
    public Func<string, Task<bool>>? RequestConfirmationAsync { get; set; }

    /// <summary>Prompts for a new name given the current one; null/unchanged means cancelled.</summary>
    public Func<string, Task<string?>>? RequestRenameAsync { get; set; }

    public Func<string, Task>? RequestCopyToClipboardAsync { get; set; }

    public Func<string, IReadOnlyList<PropertyField>, Task>? RequestShowPropertiesAsync { get; set; }

    /// <summary>Opens the "Add sync pair" wizard pre-filled with this row's local path.</summary>
    public Func<string, Task>? RequestSyncSelectedPathAsync { get; set; }

    /// <summary>Looks up the configured sync pair (if any) whose local side is a given path.</summary>
    public Func<string, SyncPairViewModel?>? FindSyncPairByPath { get; set; }

    /// <summary>
    /// The remote path a local path maps to, or null when it is outside every sync pair. Supplied
    /// by <c>MainWindowViewModel</c> so this pane needs no reference to the sync panel or to
    /// <c>PathMapper</c> (docs/PLAN-UX-ROUND-2.md §12).
    /// </summary>
    public Func<string, string?>? FindRemotePathFor { get; set; }

    /// <summary>
    /// A quick filter over the current folder's file/folder names (case-insensitive substring,
    /// same rule as the cloud pane's own search — docs/INTERFACE_IMPROVEMENT_PLAN.md §2.1's "Global
    /// Quick Search"). Reset on navigation: a search term belongs to the folder it was typed in, the
    /// same reasoning <c>MainWindowViewModel</c>'s kind filter already follows.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RenderItems();
                OnPropertyChanged(nameof(HasSearchText));
                OnPropertyChanged(nameof(SearchResultText));
                ClearSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Whether a search term is narrowing the list, for the clear button. A filter that hides rows
    /// without saying so — and with no way back but selecting the text and deleting it — was the
    /// specific complaint (docs/PLAN-UX-ROUND-2.md §9).
    /// </summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>
    /// How many rows survived the search, phrased the way the kind chips already phrase their own
    /// counts. Empty when nothing is being searched, so the label costs no space in the common case.
    /// </summary>
    public string SearchResultText
    {
        get
        {
            if (!HasSearchText)
            {
                return string.Empty;
            }

            // Same plural key the cloud pane uses: this counter was two Spanish literals built by
            // hand, in an interface that ships English by default (docs/PLAN-UX-ROUND-3.md X8).
            return Loc.Plural(StringKeys.Explorer.SearchResults, Items.Count);
        }
    }

    /// <summary>Best-effort: a home directory that can't be listed shows a status message, not a crash at startup.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await NavigateAsync(HomePath);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    public async Task NavigateAsync(string path)
    {
        IsLoading = true;
        StatusMessage = null;

        try
        {
            var items = await Task.Run(() => _service.ListDirectory(path, ShowHiddenFiles));
            _loadedItems = DriveItemSorter.Sort(items, DriveSortKey.Name, descending: false);

            CurrentPath = path;
            // A search term belongs to the folder it was typed in — carrying it into the next one
            // would hide files the user never filtered, or make an unrelated folder look empty.
            // Set through the field, not the property: the property re-renders on its own, which
            // here would just repeat the render RenderItems() below already does.
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            RenderItems();

            RebuildBreadcrumbs();
            FreeSpaceText = _service.AvailableFreeBytes(path) is { } bytes ? ByteSize.Format(bytes) : "—";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            SetStatus(StringKeys.Local.StatusOpenFailed, path, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The local pane's half of docs/PLAN-UX-ROUND-3.md X3 — the same three situations as the cloud
    /// pane, minus the kind chips this pane does not have.
    /// </summary>
    public bool IsListingEmpty => _hasRenderedListing && !IsLoading && Items.Count == 0;

    /// <summary>The folder does have contents; the search box is hiding all of them.</summary>
    public bool IsListingFilteredToNothing => IsListingEmpty && _loadedItems.Count > 0;

    public string ListingEmptyTitle => Loc.T(IsListingFilteredToNothing
        ? StringKeys.Explorer.EmptyFilteredTitle
        : StringKeys.Explorer.EmptyFolderTitle);

    public string ListingEmptyDetail => IsListingFilteredToNothing
        ? Loc.F(StringKeys.Local.EmptyFilteredDetail, _loadedItems.Count.ToString("n0", Loc.Culture))
        : Loc.T(StringKeys.Local.EmptyFolderDetail);

    private void RaiseEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsListingEmpty));
        OnPropertyChanged(nameof(IsListingFilteredToNothing));
        OnPropertyChanged(nameof(ListingEmptyTitle));
        OnPropertyChanged(nameof(ListingEmptyDetail));
    }

    private void RenderItems()
    {
        var visible = string.IsNullOrWhiteSpace(_searchText)
            ? _loadedItems
            : _loadedItems.Where(item => item.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        _selectionAnchorPath = null;
        Items.Clear();
        foreach (var item in visible)
        {
            Items.Add(new LocalNodeViewModel(item, HandleRowClickAsync, SelectRowAsync, _onError, new LocalNodeSyncActions
            {
                FindSyncPair = i => FindSyncPairByPath?.Invoke(i.Path),
                SyncSelectedPathAsync = SyncSelectedPathAsync,
                CopyPathAsync = CopyPathAsync,
                RenameAsync = RenameItemAsync,
                DeleteAsync = DeleteItemAsync,
                ShowPropertiesAsync = ShowPropertiesAsync,
                RefreshPaneAsync = () => NavigateAsync(CurrentPath),
            }));
        }

        RaiseSelectionChanged();
        OnPropertyChanged(nameof(SearchResultText));
        // See MainWindowViewModel.RenderItems: not before the first paint.
        _hasRenderedListing = true;
        RaiseEmptyStateChanged();
    }

    /// <summary>
    /// A plain click (no modifier): activates like before Task 2.2 existed — selects just this row,
    /// then opens it if it's a folder — but now also clears any multi-selection, the same way a
    /// plain click always resets a file manager's selection to "just this one."
    /// </summary>
    private async Task HandleRowClickAsync(DriveItem item)
    {
        SelectSingle(item.Path);

        if (item.IsFolder)
        {
            await NavigateAsync(item.Path);
        }
    }

    /// <summary>
    /// A plain click: selection only, matching the cloud pane (docs/PLAN-UX-ROUND-3.md X2).
    /// Opening a folder is the double click.
    /// </summary>
    private async Task SelectRowAsync(DriveItem item)
    {
        SelectSingle(item.Path);
        await Task.CompletedTask;
    }

    private void SelectSingle(string path)
    {
        foreach (var node in Items)
        {
            node.IsSelected = node.Item.Path == path;
        }

        _selectionAnchorPath = path;
        RaiseSelectionChanged();
    }

    /// <summary>Ctrl/Cmd+Click: adds or removes just this row, leaving every other row's selection untouched.</summary>
    public void ToggleSelection(LocalNodeViewModel node)
    {
        node.IsSelected = !node.IsSelected;
        _selectionAnchorPath = node.Item.Path;
        RaiseSelectionChanged();
    }

    /// <summary>Shift+Click: selects the contiguous run between the last-touched row (the anchor) and this one, replacing whatever was selected before — standard file-manager range-select.</summary>
    public void SelectRange(LocalNodeViewModel target)
    {
        var items = Items;
        var anchorIndex = _selectionAnchorPath is null ? -1 : IndexOfPath(items, _selectionAnchorPath);
        var targetIndex = items.IndexOf(target);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            SelectSingle(target.Item.Path);
            return;
        }

        var (lo, hi) = anchorIndex <= targetIndex ? (anchorIndex, targetIndex) : (targetIndex, anchorIndex);
        for (var i = 0; i < items.Count; i++)
        {
            items[i].IsSelected = i >= lo && i <= hi;
        }

        RaiseSelectionChanged();
    }

    private static int IndexOfPath(ObservableCollection<LocalNodeViewModel> items, string path)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Item.Path == path)
            {
                return i;
            }
        }

        return -1;
    }

    private Task SelectAllAsync()
    {
        foreach (var node in Items)
        {
            node.IsSelected = true;
        }

        _selectionAnchorPath = Items.Count > 0 ? Items[0].Item.Path : null;
        RaiseSelectionChanged();
        return Task.CompletedTask;
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(SelectionSummaryText));
        SelectAllCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The batch counterpart to <see cref="DeleteItemAsync"/> — one confirmation for the whole selection, then each item deleted independently so one failure doesn't abandon the rest.</summary>
    private async Task DeleteSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var confirm = RequestConfirmationAsync;
        var question = selected.Count == 1
            ? Loc.F(StringKeys.Local.ConfirmDeleteOne, selected[0].DisplayName)
            : Loc.Plural(StringKeys.Local.ConfirmDeleteMany, selected.Count);

        if (confirm is not null && !await confirm(question))
        {
            SetStatus(StringKeys.Local.StatusDeleteCancelled);
            return;
        }

        var failures = new List<string>();
        foreach (var node in selected)
        {
            try
            {
                _service.Delete(node.Item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{node.DisplayName}: {ex.Message}");
            }
        }

        SetStatus(failures.Count == 0
            ? LocalizedText.Plural(StringKeys.Local.StatusDeletedMany, selected.Count)
            : LocalizedText.Of(StringKeys.Local.StatusDeletedPartial, selected.Count - failures.Count, selected.Count, string.Join("; ", failures)));

        await NavigateAsync(CurrentPath);
    }

    private Task GoBackAsync()
    {
        var parent = System.IO.Path.GetDirectoryName(CurrentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) ? Task.CompletedTask : NavigateAsync(parent);
    }

    private Task ToggleHiddenFilesAsync()
    {
        ShowHiddenFiles = !ShowHiddenFiles;
        _settings.Update(s => s.ShowHiddenLocalFiles = ShowHiddenFiles);
        return NavigateAsync(CurrentPath);
    }

    private void RebuildBreadcrumbs()
    {
        BreadcrumbItems.Clear();

        var root = System.IO.Path.GetPathRoot(CurrentPath) ?? CurrentPath;
        var segments = new List<(string Label, string Path)> { (root, root) };

        var relative = CurrentPath[root.Length..].Trim(System.IO.Path.DirectorySeparatorChar);
        if (!string.IsNullOrEmpty(relative))
        {
            // Combine from the untouched root, not a trimmed copy: trimming "/" itself down to ""
            // would turn every combine below into a relative path ("tmp" instead of "/tmp").
            var accumulated = root;
            foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = System.IO.Path.Combine(accumulated, part);
                segments.Add((part, accumulated));
            }
        }

        foreach (var (label, path) in segments)
        {
            var isCurrent = PathsEqual(path, CurrentPath);
            BreadcrumbItems.Add(new BreadcrumbSegmentViewModel(label, path, isCurrent, NavigateAsync, _onError));
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd(System.IO.Path.DirectorySeparatorChar), b.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.Ordinal);

    /// <summary>Permanently deletes a local file/folder — there is no local trash to fall back into, so this always confirms first.</summary>
    public async Task DeleteItemAsync(DriveItem item)
    {
        var confirm = RequestConfirmationAsync;
        var question = Loc.F(
            item.IsFolder ? StringKeys.Local.ConfirmDeleteFolder : StringKeys.Local.ConfirmDeleteOne,
            item.Name);

        if (confirm is not null && !await confirm(question))
        {
            SetStatus(StringKeys.Local.StatusDeleteCancelledOne, item.Name);
            return;
        }

        try
        {
            _service.Delete(item.Path);
            SetStatus(StringKeys.Local.StatusDeletedOne, item.Name);
            await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(StringKeys.Local.StatusDeleteFailed, item.Name, ex.Message);
        }
    }

    public async Task RenameItemAsync(DriveItem item)
    {
        var requester = RequestRenameAsync;
        if (requester is null)
        {
            SetStatus(StringKeys.Status.RenameUnavailable);
            return;
        }

        var newName = await requester(item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            return;
        }

        try
        {
            _service.Rename(item.Path, newName);
            SetStatus(StringKeys.Status.RenameDone, item.Name, newName);
            await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(StringKeys.Local.StatusRenameFailed, item.Name, ex.Message);
        }
    }

    public async Task CopyPathAsync(DriveItem item)
    {
        var copy = RequestCopyToClipboardAsync;
        if (copy is null)
        {
            SetStatus(StringKeys.Status.CopyUnavailable);
            return;
        }

        await copy(item.Path);
        StatusMessage = $"Ruta copiada: {item.Path}";
    }

    public async Task SyncSelectedPathAsync(DriveItem item)
    {
        if (!item.IsFolder)
        {
            return;
        }

        var handler = RequestSyncSelectedPathAsync;
        if (handler is null)
        {
            SetStatus(StringKeys.Local.StatusSyncUnavailable);
            return;
        }

        await handler(item.Path);
    }

    public async Task ShowPropertiesAsync(DriveItem item)
    {
        var show = RequestShowPropertiesAsync;
        if (show is null)
        {
            return;
        }

        var fields = new List<PropertyField>
        {
            new(Loc.T(StringKeys.Common.Name), item.Name),
            new(Loc.T(StringKeys.Common.Path), item.Path, IsCopyable: true),
            new(Loc.T(StringKeys.Common.Type), Loc.T(item.IsFolder ? StringKeys.Common.Folder : StringKeys.Common.File)),
        };

        // The mirror of the remote pane's "Ruta local" (docs/PLAN-UX-ROUND-2.md §12). These two
        // panes were made consistent by construction in §10; shipping half of this feature would
        // put the asymmetry straight back.
        if (FindRemotePathFor?.Invoke(item.Path) is { } remotePath)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Explorer.RemotePath), remotePath, IsCopyable: true));
        }

        if (item.Size is not null)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Common.Size), ByteSize.Format(item.Size.Value)));
        }

        if (item.ModifiedAt is not null)
        {
            fields.Add(new PropertyField(Loc.T(StringKeys.Common.Modified), item.ModifiedAt.Value.ToLocalTime().ToString("g", Loc.Culture)));
        }

        await show(item.Name, fields);
    }
}
