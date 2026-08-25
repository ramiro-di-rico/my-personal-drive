using System.Collections.ObjectModel;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;

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
    private string _headline = "Sin datos.";
    private string _totalSizeText = "—";
    private string _scopeNote = string.Empty;
    private string _newestText = "—";
    private string _oldestText = "—";
    private bool _hasItems;
    private bool _hasBuckets;
    private bool _hasLargestItems;

    public FolderMetricsViewModel(Func<DriveItem, Task> selectItem, Action<Exception>? onError = null)
    {
        _selectItem = selectItem;
        _onError = onError;
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

    public void Update(FolderMetrics metrics)
    {
        HasItems = !metrics.IsEmpty;
        Headline = metrics.IsEmpty
            ? "Carpeta vacía."
            : $"{Plural(metrics.FileCount, "archivo", "archivos")} · {Plural(metrics.FolderCount, "carpeta", "carpetas")}";
        TotalSizeText = ByteSize.Format(metrics.TotalSize);
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

    private static string BuildScopeNote(FolderMetrics metrics)
    {
        if (metrics.IsEmpty)
        {
            return string.Empty;
        }

        var notes = new List<string>();
        if (!metrics.IsDeep && metrics.FolderCount > 0)
        {
            notes.Add($"no incluye el contenido de {Plural(metrics.FolderCount, "subcarpeta", "subcarpetas")}");
        }

        if (metrics.UnknownSizeCount > 0)
        {
            notes.Add($"{Plural(metrics.UnknownSizeCount, "archivo", "archivos")} sin tamaño conocido");
        }

        if (notes.Count == 0)
        {
            return "Tamaño declarado al subir los archivos.";
        }

        var joined = string.Join("; ", notes);
        return $"{char.ToUpperInvariant(joined[0])}{joined[1..]}.";
    }

    private static string Plural(int count, string singular, string plural)
        => count == 1 ? $"1 {singular}" : $"{count:n0} {plural}";

    private static string FormatTimestamp(DateTimeOffset? value)
        => value is { } timestamp ? timestamp.ToLocalTime().ToString("g") : "—";
}
