using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MyPersonalDrive.Views;

/// <summary>
/// The path bar shared by the remote and local panes (docs/PLAN-UX-ROUND-2.md §10). They were two
/// copies of the same markup, differing only in their heading, sharing only
/// <c>BreadcrumbSegmentViewModel</c> — mirror panes that did not look like mirrors.
///
/// The scroll-to-end behaviour moved in here from <see cref="MainWindow"/>'s code-behind, where it
/// was wired to the remote bar by <c>x:Name</c> and so could only ever apply to one of the two.
/// </summary>
public partial class BreadcrumbBar : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<BreadcrumbBar, IEnumerable?>(nameof(ItemsSource));

    /// <summary>The heading above the segments — the provider's name, or the local pane's own label. Localizable, so it is bound rather than set (docs/PLAN-UX-ROUND-3.md X8).</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<BreadcrumbBar, string?>(nameof(Label));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<BreadcrumbBar, Geometry?>(nameof(Icon));

    private INotifyCollectionChanged? _observed;

    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ItemsSourceProperty)
        {
            return;
        }

        // Re-subscribed rather than subscribed once: the panes hand over a different collection when
        // the browsed account switches, and a stale subscription would scroll the wrong bar.
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnSegmentsChanged;
        }

        _observed = change.NewValue as INotifyCollectionChanged;
        if (_observed is not null)
        {
            _observed.CollectionChanged += OnSegmentsChanged;
        }
    }

    /// <summary>
    /// Deep paths would otherwise overflow the bar's fixed width with no way to see the folder
    /// you're actually in. Rather than truncating segments (which hides the middle of the path you
    /// might want to click back into), it scrolls — and always to the current folder, which is what
    /// you care about after navigating. Posted after the items collection actually changes so the
    /// ScrollViewer's Extent already reflects the new content; setting Offset past the max clamps
    /// to the real end.
    /// </summary>
    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(
            () => SegmentScroll.Offset = new Vector(double.MaxValue, 0),
            DispatcherPriority.Background);
}
