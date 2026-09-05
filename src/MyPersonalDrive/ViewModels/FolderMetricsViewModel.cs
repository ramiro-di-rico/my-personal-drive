using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// One row of the metrics histogram.
///
/// <see cref="BarWidth"/> is in pixels, which is not something a view model would normally know.
/// The alternative is binding a <c>GridLength</c> from here, and that would put an Avalonia type in
/// a view model (forbidden by AGENTS.md); a converter can't do it either, because the proportion
/// depends on the other buckets, not just this one. So the width is computed against
/// <see cref="BarMaxWidth"/>, which must stay in step with the metrics panel's content width in
/// <c>MainWindow.axaml</c>.
/// </summary>
public sealed class FolderMetricBucketViewModel
{
    public const double BarMaxWidth = 260;

    public FolderMetricBucketViewModel(FolderKindBucket bucket, long largestBucketSize)
    {
        Kind = bucket.Kind;
        Label = FileKindClassifier.DisplayName(bucket.Kind);
        Count = bucket.Count;
        TotalSize = bucket.TotalSize;

        // Scaled against the biggest bucket, not against the folder total: with one dominant type
        // every other bar would collapse to a sliver and the chart would say nothing.
        BarWidth = largestBucketSize > 0
            ? Math.Max(2, BarMaxWidth * bucket.TotalSize / largestBucketSize)
            : 2;
    }

    public FileKind Kind { get; }

    public string Label { get; }

    public int Count { get; }

    public long TotalSize { get; }

    public double BarWidth { get; }

    public string CountText => Count == 1 ? "1 elemento" : $"{Count:n0} elementos";

    public string SizeText => TotalSize > 0 ? ByteSize.Format(TotalSize) : "—";
}

/// <summary>
/// A clickable "largest item" row.
/// </summary>
public sealed class LargestItemViewModel
{
    public LargestItemViewModel(DriveItem item, Func<DriveItem, Task> select, Action<Exception>? onError)
    {
        Item = item;
        SelectCommand = new AsyncCommand(() => select(item), onError: onError);
    }

    public DriveItem Item { get; }

    public string Name => Item.Name;

    public string SizeText => Item.Size is { } size ? ByteSize.Format(size) : "—";

    public AsyncCommand SelectCommand { get; }
}

/// <summary>
/// The metrics section of the side panel (docs/PLAN-BROWSER-VIEWS.md M5). Shallow metrics only for
/// now — recomputed from the listing on every folder load, at no CLI cost. Deep (recursive) metrics
/// are M3/M4 and are not wired up yet, which is why <see cref="ScopeNote"/> always says the total
/// covers this folder's own files.
/// </summary>
public sealed class FolderMetricsViewModel : ObservableObject
{
    private readonly Func<DriveItem, Task> _selectItem;
    private readonly Action<Exception>? _onError;
    private readonly TimeProvider _timeProvider;
    private string _headline = Localizer.Instance.T(StringKeys.Metrics.NoData);
    private string _totalSizeText = "—";
    private string _totalSizeCaption = Localizer.Instance.T(StringKeys.Metrics.CaptionThisFolder);
    private string _scopeNote = string.Empty;
    private string _newestText = "—";
    private string _oldestText = "—";
    private bool _hasItems;
    private bool _hasBuckets;
    private bool _hasLargestItems;
    private bool _isDeep;
    private bool _isPartial;
    private string _depthNote = string.Empty;
    private string _progressText = string.Empty;
    private bool _isScanning;

    public FolderMetricsViewModel(Func<DriveItem, Task> selectItem, Action<Exception>? onError = null, TimeProvider? timeProvider = null)
    {
        _selectItem = selectItem;
        _onError = onError;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Buckets = new ObservableCollection<FolderMetricBucketViewModel>();
        LargestItems = new ObservableCollection<LargestItemViewModel>();
    }

    public ObservableCollection<FolderMetricBucketViewModel> Buckets { get; }

    public ObservableCollection<LargestItemViewModel> LargestItems { get; }

    /// <summary>"12 archivos · 3 carpetas".</summary>
    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set => SetProperty(ref _totalSizeText, value);
    }

    /// <summary>
    /// What the big number covers. Lives here rather than as fixed text in the view because the
    /// answer changes with the metric's depth, and a shallow total labelled as a folder's size is
    /// exactly the mistake this feature has to avoid.
    /// </summary>
    public string TotalSizeCaption
    {
        get => _totalSizeCaption;
        private set => SetProperty(ref _totalSizeCaption, value);
    }

    /// <summary>
    /// What the total does <em>not</em> include. Never empty when there are subfolders or
    /// unknown-size files: a bare total next to a folder listing reads as recursive, and this app
    /// cannot produce a recursive total without a multi-minute scan.
    /// </summary>
    public string ScopeNote
    {
        get => _scopeNote;
        private set => SetProperty(ref _scopeNote, value);
    }

    public string NewestText
    {
        get => _newestText;
        private set => SetProperty(ref _newestText, value);
    }

    public string OldestText
    {
        get => _oldestText;
        private set => SetProperty(ref _oldestText, value);
    }

    public bool HasItems
    {
        get => _hasItems;
        private set => SetProperty(ref _hasItems, value);
    }

    public bool HasBuckets
    {
        get => _hasBuckets;
        private set => SetProperty(ref _hasBuckets, value);
    }

    public bool HasLargestItems
    {
        get => _hasLargestItems;
        private set => SetProperty(ref _hasLargestItems, value);
    }

    /// <summary>Whether what's on screen came from a recursive scan.</summary>
    public bool IsDeep
    {
        get => _isDeep;
        private set => SetProperty(ref _isDeep, value);
    }

    /// <summary>A cancelled scan's numbers: real, but a floor rather than a total.</summary>
    public bool IsPartial
    {
        get => _isPartial;
        private set => SetProperty(ref _isPartial, value);
    }

    /// <summary>
    /// Where the number came from and how old it is — "recursivo · 412 carpetas · calculado hace
    /// 3 días". Shown because a deep total cost minutes and the user has to be able to judge
    /// whether it's still worth trusting; the app deliberately does not silently expire it.
    /// </summary>
    public string DepthNote
    {
        get => _depthNote;
        private set => SetProperty(ref _depthNote, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public void BeginDeepScan()
    {
        IsScanning = true;
        ProgressText = "Analizando carpetas...";
    }

    public void ReportDeepScanProgress(int foldersScanned, int foldersQueued)
    {
        var localizer = Localizer.Instance;
        var scanned = localizer.Plural(StringKeys.Metrics.ProgressScanned, foldersScanned);
        ProgressText = foldersQueued > 0
            ? localizer.F(StringKeys.Metrics.ProgressQueued, scanned, foldersQueued.ToString("n0", localizer.Culture))
            : scanned;
    }

    public void EndDeepScan()
    {
        IsScanning = false;
        ProgressText = string.Empty;
    }

    public void Update(FolderMetrics metrics)
    {
        IsDeep = metrics.IsDeep;
        IsPartial = !metrics.IsComplete;
        DepthNote = BuildDepthNote(metrics, _timeProvider.GetUtcNow());
        HasItems = !metrics.IsEmpty;
        Headline = metrics.IsEmpty
            ? Loc.T(StringKeys.Metrics.EmptyFolder)
            : Loc.F(
                StringKeys.Metrics.Headline,
                Loc.Plural(StringKeys.Metrics.Files, metrics.FileCount),
                Loc.Plural(StringKeys.Metrics.Folders, metrics.FolderCount));
        TotalSizeText = ByteSize.Format(metrics.TotalSize);
        TotalSizeCaption = Loc.T(metrics.IsDeep
            ? (metrics.IsComplete ? StringKeys.Metrics.CaptionTotal : StringKeys.Metrics.CaptionPartial)
            : StringKeys.Metrics.CaptionThisFolder);
        ScopeNote = BuildScopeNote(metrics);
        NewestText = FormatTimestamp(metrics.NewestModifiedAt);
        OldestText = FormatTimestamp(metrics.OldestModifiedAt);

        Buckets.Clear();
        var largestBucketSize = metrics.Buckets.Count > 0 ? metrics.Buckets.Max(bucket => bucket.TotalSize) : 0;
        foreach (var bucket in metrics.Buckets)
        {
            Buckets.Add(new FolderMetricBucketViewModel(bucket, largestBucketSize));
        }

        HasBuckets = Buckets.Count > 0;

        LargestItems.Clear();
        foreach (var item in metrics.LargestItems)
        {
            LargestItems.Add(new LargestItemViewModel(item, _selectItem, _onError));
        }

        HasLargestItems = LargestItems.Count > 0;
    }

    private static string BuildDepthNote(FolderMetrics metrics, DateTimeOffset now)
    {
        if (!metrics.IsDeep)
        {
            return string.Empty;
        }

        var localizer = Localizer.Instance;
        var scope = localizer.Plural(
            metrics.IsComplete ? StringKeys.Metrics.ScopeRecursive : StringKeys.Metrics.ScopePartial,
            metrics.ScannedFolderCount);

        return localizer.F(StringKeys.Metrics.DepthNote, scope, Age(now - metrics.ComputedAt));
    }

    /// <summary>
    /// Coarse on purpose: the useful question about a deep metric is "is this from today or from
    /// last month", not the minute it was produced.
    /// </summary>
    private static string Age(TimeSpan elapsed)
    {
        var localizer = Localizer.Instance;
        if (elapsed < TimeSpan.FromMinutes(2))
        {
            return localizer.T(StringKeys.Metrics.AgeJustNow);
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return localizer.F(StringKeys.Metrics.AgeMinutes, (int)elapsed.TotalMinutes);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return localizer.F(StringKeys.Metrics.AgeHours, (int)elapsed.TotalHours);
        }

        return localizer.Plural(StringKeys.Metrics.AgeDays, (int)elapsed.TotalDays);
    }

    private static string BuildScopeNote(FolderMetrics metrics)
    {
        if (metrics.IsEmpty)
        {
            return string.Empty;
        }

        var notes = new List<string>();
        if (!metrics.IsDeep && metrics.FolderCount > 0)
        {
            notes.Add(Localizer.Instance.F(StringKeys.Metrics.ScopeExcludes, Localizer.Instance.Plural(StringKeys.Metrics.Subfolders, metrics.FolderCount)));
        }

        if (metrics.UnknownSizeCount > 0)
        {
            notes.Add(Localizer.Instance.F(StringKeys.Metrics.ScopeUnknownSize, Localizer.Instance.Plural(StringKeys.Metrics.Files, metrics.UnknownSizeCount)));
        }

        if (notes.Count == 0)
        {
            return Localizer.Instance.T(StringKeys.Metrics.ScopeDeclared);
        }

        var joined = string.Join("; ", notes);
        return $"{char.ToUpperInvariant(joined[0])}{joined[1..]}.";
    }

    private static string FormatTimestamp(DateTimeOffset? value)
        => value is { } timestamp ? timestamp.ToLocalTime().ToString("g", Localizer.Instance.Culture) : "—";
}
