using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The in-app file viewer: text, images and PDFs, with the zoom that applies to the last two.
///
/// Z5 step 3 (docs/PLAN-UX-ROUND-4.md#z5). The third cluster out of the view model. It owns the
/// three preview loaders, the cancellation source for an in-flight preview — which was already
/// disposed correctly here while two others in the parent were not (Z2) — and the panel's own
/// state. What it needs from outside is somewhere to report and a way to download, both handed in.
///
/// The loaders are settable rather than injected once: switching browser account swaps all three.
/// </summary>
public sealed class FilePreviewViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly StatusSurface _status;

    /// <summary>
    /// How a provider failure is reported: the parent owns FormatDriveError, which turns a
    /// DriveException into the sentence the user reads and records the kind the alert strip's
    /// remedy is chosen from (docs/PLAN-UX-ROUND-4.md Y3).
    /// </summary>
    private readonly Action<string, Exception> _reportFailure;

    private ITextFilePreviewLoader? _previewLoader;
    private IImageFilePreviewLoader? _imagePreviewLoader;
    private IPdfFilePreviewLoader? _pdfPreviewLoader;
    private CancellationTokenSource? _previewCts;

    private bool _isViewerVisible;
    private bool _isViewerLoading;
    private string _viewerTitle = Localizer.Instance.T(StringKeys.Viewer.Title);
    private string _viewerPath = string.Empty;
    private string _viewerText = string.Empty;
    private string _viewerNote = string.Empty;
    private byte[]? _viewerImageBytes;
    private IReadOnlyList<byte[]>? _viewerPdfPages;
    private double _viewerZoom;

    internal FilePreviewViewModel(
        AppSettingsService settings,
        StatusSurface status,
        ITextFilePreviewLoader? previewLoader,
        IImageFilePreviewLoader? imagePreviewLoader,
        IPdfFilePreviewLoader? pdfPreviewLoader,
        Action<string, Exception> reportFailure,
        Action<Exception> onError)
    {
        _settings = settings;
        _status = status;
        _reportFailure = reportFailure;
        _previewLoader = previewLoader;
        _imagePreviewLoader = imagePreviewLoader;
        _pdfPreviewLoader = pdfPreviewLoader;
        _viewerZoom = settings.Load().ViewerZoomOrDefault();

        CloseViewerCommand = new AsyncCommand(CloseViewerAsync, onError: onError);
    }

    public AsyncCommand CloseViewerCommand { get; }

    /// <summary>Whether any loader is configured at all — nothing can be shown without one.</summary>
    public bool CanShowAnything => _previewLoader is not null || _imagePreviewLoader is not null || _pdfPreviewLoader is not null;

    /// <summary>Switching browser account swaps all three loaders at once.</summary>
    public void SetLoaders(ITextFilePreviewLoader? text, IImageFilePreviewLoader? image, IPdfFilePreviewLoader? pdf)
    {
        _previewLoader = text;
        _imagePreviewLoader = image;
        _pdfPreviewLoader = pdf;
    }

    /// <summary>Re-renders the placeholder title after a language change (docs/PLAN-UX-ROUND-4.md Y7).</summary>
    public void OnLanguageChanged()
    {
        if (!IsViewerVisible)
        {
            ViewerTitle = Localizer.Instance.T(StringKeys.Viewer.Title);
        }

        OnAllPropertiesChanged();
    }

    public string ViewerText
    {
        get => _viewerText;
        private set
        {
            if (SetProperty(ref _viewerText, value))
            {
                OnPropertyChanged(nameof(HasViewerText));
            }
        }
    }

    /// <summary>
    /// The previewed image's raw bytes, undecoded — decoding is a view concern (view models never
    /// touch Avalonia types, AGENTS.md), so the view turns this into a <c>Bitmap</c> via
    /// <c>Views.Converters.BytesToBitmapConverter</c>.
    /// </summary>
    public byte[]? ViewerImageBytes
    {
        get => _viewerImageBytes;
        private set
        {
            if (SetProperty(ref _viewerImageBytes, value))
            {
                OnPropertyChanged(nameof(HasViewerImage));
                OnPropertyChanged(nameof(HasViewerZoomableContent));
            }
        }
    }

    /// <summary>
    /// One PNG-encoded bitmap per rendered PDF page, undecoded for the same reason as
    /// <see cref="ViewerImageBytes"/> — the View decodes each entry with the same
    /// <c>BytesToBitmapConverter</c>.
    /// </summary>
    public IReadOnlyList<byte[]>? ViewerPdfPages
    {
        get => _viewerPdfPages;
        private set
        {
            if (SetProperty(ref _viewerPdfPages, value))
            {
                OnPropertyChanged(nameof(HasViewerPdf));
                OnPropertyChanged(nameof(HasViewerZoomableContent));
            }
        }
    }

    public string ViewerTitle
    {
        get => _viewerTitle;
        private set => SetProperty(ref _viewerTitle, value);
    }

    public string ViewerPath
    {
        get => _viewerPath;
        private set => SetProperty(ref _viewerPath, value);
    }

    /// <summary>
    /// The line under the viewer's toolbar: size, encoding, and — when it applies — that what's on
    /// screen is only the beginning of the file. Never silently truncate.
    /// </summary>
    public string ViewerNote
    {
        get => _viewerNote;
        private set => SetProperty(ref _viewerNote, value);
    }

    /// <summary>
    /// The image/PDF viewer's display scale — see <see cref="AppSettings.ViewerZoom"/> for why the
    /// default isn't 1.0. Clamped the same way on every write, not just on load, since the slider
    /// itself is already range-limited but a value set some other way (a future keyboard shortcut,
    /// say) shouldn't be able to hand the view something degenerate.
    /// </summary>
    public double ViewerZoom
    {
        get => _viewerZoom;
        set
        {
            // Not persisted here: AppSettingsService.Update reads settings.json and writes it
            // back, and a slider raises this on every intermediate value of a drag
            // (docs/PLAN-UX-ROUND-4.md Y6). The view commits it when the drag ends, and closing the
            // viewer commits it too, so a zoom set with the keyboard is not lost either.
            SetProperty(ref _viewerZoom, Math.Clamp(value, AppSettings.MinViewerZoom, AppSettings.MaxViewerZoom));
        }
    }

    /// <summary>
    /// Whether the in-app text viewer is open over the listing. The viewer is a panel and not a
    /// separate window so it can't get lost behind the main one, and so closing it needs no
    /// window plumbing in code-behind.
    /// </summary>
    public bool IsViewerVisible
    {
        get => _isViewerVisible;
        private set => SetProperty(ref _isViewerVisible, value);
    }

    public bool IsViewerLoading
    {
        get => _isViewerLoading;
        private set => SetProperty(ref _isViewerLoading, value);
    }

    public bool HasViewerText => _viewerText.Length > 0;

    public bool HasViewerImage => _viewerImageBytes is { Length: > 0 };

    public bool HasViewerPdf => _viewerPdfPages is { Count: > 0 };

    /// <summary>Whether the zoom control has anything to act on — hidden for the text viewer, which sizes by font instead.</summary>
    public bool HasViewerZoomableContent => HasViewerImage || HasViewerPdf;

    /// <summary>
    /// Opens the viewer on <paramref name="item"/> — as text or as an image, whichever
    /// <see cref="ImagePreviewPolicy"/>/<see cref="TextPreviewPolicy"/> say it is. Images are
    /// checked first: an image's <see cref="FileKind"/> never also qualifies as text, so the order
    /// only matters for the refusal message when neither policy accepts the file.
    /// </summary>
    public async Task PreviewItemAsync(DriveItem item)
    {
        if (!item.IsFolder && FileKindClassifier.Classify(item.Name, isFolder: false) == FileKind.Image && ImagePreviewPolicy.CanPreview(item))
        {
            await PreviewImageAsync(item);
            return;
        }

        if (PdfPreviewPolicy.CanPreview(item))
        {
            await PreviewPdfAsync(item);
            return;
        }

        if (TextPreviewPolicy.CanPreview(item))
        {
            await PreviewTextAsync(item);
            return;
        }

        _status.Set(LocalizedText.Of(
            StringKeys.Status.ViewerUnsupported,
            item.Name,
            TextPreviewPolicy.MaxPreviewBytes / 1024,
            ImagePreviewPolicy.MaxPreviewBytes / (1024 * 1024),
            PdfPreviewPolicy.MaxPreviewBytes / (1024 * 1024)));
        _status.Warn();
    }

    /// <summary>
    /// The text half of <see cref="PreviewItemAsync"/>. The CLI can only download, so this pays for
    /// a real download of the file into a temp folder that the loader deletes again.
    /// </summary>
    private async Task PreviewTextAsync(DriveItem item)
    {
        if (_previewLoader is null)
        {
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerTextUnavailable));
            _status.Warn();
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _previewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (preview.IsBinary)
            {
                ViewerText = string.Empty;
                ViewerNote = Localizer.Instance.F(StringKeys.Viewer.NotAText, preview.ByteCount.ToString("n0", Localizer.Instance.Culture));
                _status.Set(LocalizedText.Of(StringKeys.Status.ViewerNotAText, item.Name));
                _status.Warn();
                return;
            }

            ViewerText = preview.Text;
            ViewerNote = FormatViewerNote(preview);
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerShowing, item.Name));
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerOpenFailed);
            _reportFailure(item.Path, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerReadFailed);
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render()));
            _status.Warn();
        }
        finally
        {
            EndPreview(cts);
        }
    }

    private async Task PreviewImageAsync(DriveItem item)
    {
        if (_imagePreviewLoader is null)
        {
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerImageUnavailable));
            _status.Warn();
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _imagePreviewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ViewerImageBytes = preview.Bytes;
            ViewerNote = Localizer.Instance.F(StringKeys.Viewer.NoteBytes, preview.ByteCount.ToString("n0", Localizer.Instance.Culture));
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerShowing, item.Name));
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerOpenFailed);
            _reportFailure(item.Path, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerReadFailed);
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render()));
            _status.Warn();
        }
        finally
        {
            EndPreview(cts);
        }
    }

    private async Task PreviewPdfAsync(DriveItem item)
    {
        if (_pdfPreviewLoader is null)
        {
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerPdfUnavailable));
            _status.Warn();
            return;
        }

        var cts = BeginPreview(item);

        try
        {
            var preview = await _pdfPreviewLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ViewerPdfPages = preview.Pages;
            ViewerNote = preview.Pages.Count < preview.TotalPageCount
                ? Localizer.Instance.F(StringKeys.Viewer.NotePages, preview.Pages.Count, preview.TotalPageCount)
                : Loc.Plural(StringKeys.Viewer.NotePageCount, preview.TotalPageCount);
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerShowing, item.Name));
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed; whoever did that already owns the panel's state.
        }
        catch (InvalidOperationException ex)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerOpenFailed);
            _reportFailure(item.Path, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewerNote = Localizer.Instance.T(StringKeys.Status.ViewerReadFailed);
            _status.Set(LocalizedText.Of(StringKeys.Status.ViewerError, item.Name, ex.DescribeForUser().Render()));
            _status.Warn();
        }
        finally
        {
            EndPreview(cts);
        }
    }

    /// <summary>
    /// Shared setup for both preview flows: supersede any in-flight download and reset the panel to
    /// a clean loading state for <paramref name="item"/>, clearing whichever content type the
    /// previous preview left behind.
    /// </summary>
    private CancellationTokenSource BeginPreview(DriveItem item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        IsViewerVisible = true;
        IsViewerLoading = true;
        ViewerTitle = item.Name;
        ViewerPath = item.Path;
        ViewerText = string.Empty;
        ViewerImageBytes = null;
        ViewerPdfPages = null;
        ViewerNote = Localizer.Instance.T(StringKeys.Viewer.NoteDownloading);
        _status.Set(LocalizedText.Of(StringKeys.Status.ViewerOpening, item.Name));
        return cts;
    }

    private void EndPreview(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_previewCts, cts))
        {
            IsViewerLoading = false;
            _previewCts = null;
            cts.Dispose();
        }
    }

    private async Task CloseViewerAsync()
    {
        CommitViewerZoom();

        _previewCts?.Cancel();
        IsViewerVisible = false;
        IsViewerLoading = false;
        ViewerImageBytes = null;
        ViewerPdfPages = null;
        ViewerText = string.Empty;
        ViewerNote = string.Empty;
        await Task.CompletedTask;
    }

    /// <summary>Writes the zoom once, when the gesture that changed it ends. See <see cref="ViewerZoom"/>.</summary>
    public void CommitViewerZoom()
        => _settings.Update(s => s.ViewerZoom = _viewerZoom);


    private static string FormatViewerNote(TextFilePreview preview)
    {
        // "más de" when the read stopped at the byte limit: ByteCount is what was read, not the
        // file's size, and printing it as the size would be a lie of exactly one byte.
        var localizer = Localizer.Instance;
        var size = preview.ByteCount > TextPreviewPolicy.MaxPreviewBytes
            ? localizer.F(StringKeys.Viewer.NoteMoreThan, TextPreviewPolicy.MaxPreviewBytes.ToString("n0", localizer.Culture))
            : localizer.F(StringKeys.Viewer.NoteBytes, preview.ByteCount.ToString("n0", localizer.Culture));
        var note = localizer.F(StringKeys.Viewer.NoteText, preview.LineCount.ToString("n0", localizer.Culture), size, preview.EncodingName);
        return preview.IsTruncated
            ? note + localizer.T(StringKeys.Viewer.NoteTruncated)
            : note;
    }
}
