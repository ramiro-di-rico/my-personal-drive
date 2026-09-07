using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Local;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels.Local;

/// <summary>
/// The local pane's PDF merge. Locally there is nothing to download, so what this has to prove is
/// the parts the merge service itself cannot: only PDFs are counted, the command stays disabled
/// below two, the dialog's reordering reaches the service as the source order, and a failure ends
/// up on the status line rather than escaping the AsyncCommand.
/// </summary>
[Collection(AppDataCollection.Name)]
public class LocalExplorerPdfMergeTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalMerge.AppData").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalMerge.Root").FullName;
    private readonly string? _originalAppData;

    public LocalExplorerPdfMergeTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_root, name), "x");
        }
    }

    private async Task<(LocalExplorerViewModel Sut, FakePdfMergeService Merge)> BuildAsync()
    {
        var merge = new FakePdfMergeService();
        var sut = new LocalExplorerViewModel(
            new FakeHomeLocalFileSystemService(_root),
            new AppSettingsService(),
            pdfMerge: merge);
        await sut.NavigateAsync(_root);
        return (sut, merge);
    }

    private static void Select(LocalExplorerViewModel sut, params string[] names)
    {
        foreach (var name in names)
        {
            sut.ToggleSelection(sut.Items.Single(i => i.Item.Name == name));
        }
    }

    [Fact]
    public async Task SelectedMergeableCount_counts_images_alongside_pdfs()
    {
        Touch("scan.pdf", "photo.jpg", "logo.png", "notes.txt", "raw.cr2");
        var (sut, _) = await BuildAsync();

        Select(sut, "scan.pdf", "photo.jpg", "logo.png", "notes.txt", "raw.cr2");

        // The text file does not count, and neither does a RAW image — the merge accepts exactly
        // the formats the image viewer can already decode, no wider.
        Assert.Equal(5, sut.SelectedCount);
        Assert.Equal(3, sut.SelectedMergeableCount);
        Assert.True(sut.CanMergeSelected);
    }

    [Fact]
    public async Task MergeAsync_accepts_a_selection_of_images_with_no_pdf_in_it()
    {
        Touch("one.jpg", "two.png");
        var (sut, merge) = await BuildAsync();
        Select(sut, "one.jpg", "two.png");
        sut.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "album.pdf"));

        await sut.MergeSelectedCommand.ExecuteAsync();

        var (sources, output) = Assert.Single(merge.Merges);
        Assert.Equal(2, sources.Count);
        Assert.Equal(Path.Combine(_root, "album.pdf"), output);
    }

    [Fact]
    public async Task SelectedMergeableCount_counts_only_mergeable_files()
    {
        Touch("a.pdf", "b.PDF", "notes.txt");
        var (sut, _) = await BuildAsync();

        Select(sut, "a.pdf", "b.PDF", "notes.txt");

        // Three rows selected, but only two of them can be merged — and the extension check is
        // case-insensitive, which ".PDF" is here to prove.
        Assert.Equal(3, sut.SelectedCount);
        Assert.Equal(2, sut.SelectedMergeableCount);
        Assert.True(sut.CanMergeSelected);
        Assert.True(sut.MergeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeCommand_stays_disabled_with_a_single_mergeable_file_selected()
    {
        Touch("a.pdf", "notes.txt");
        var (sut, _) = await BuildAsync();

        Select(sut, "a.pdf", "notes.txt");

        Assert.Equal(1, sut.SelectedMergeableCount);
        Assert.False(sut.CanMergeSelected);
        Assert.False(sut.MergeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeAsync_passes_the_dialog_order_through_to_the_merge_service()
    {
        Touch("a.pdf", "b.pdf", "c.pdf");
        var (sut, merge) = await BuildAsync();
        Select(sut, "a.pdf", "b.pdf", "c.pdf");

        // The dialog reordered to c, a and dropped b — both things its buttons can do.
        sut.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([2, 0], "joined.pdf"));

        await sut.MergeSelectedCommand.ExecuteAsync();

        var (sources, output) = Assert.Single(merge.Merges);
        Assert.Equal([Path.Combine(_root, "c.pdf"), Path.Combine(_root, "a.pdf")], sources);
        Assert.Equal(Path.Combine(_root, "joined.pdf"), output);
    }

    [Fact]
    public async Task MergeAsync_suggests_a_name_derived_from_the_first_selected_pdf()
    {
        Touch("report.pdf", "annex.pdf");
        var (sut, _) = await BuildAsync();
        Select(sut, "report.pdf", "annex.pdf");

        string? suggested = null;
        sut.RequestPdfMergeAsync = (_, name) =>
        {
            suggested = name;
            return Task.FromResult<PdfMergeRequest?>(null);
        };

        await sut.MergeSelectedCommand.ExecuteAsync();

        // Never empty, and never the name of a source — which would propose overwriting one.
        Assert.Equal("annex-merged.pdf", suggested);
    }

    [Fact]
    public async Task MergeAsync_does_nothing_when_the_dialog_is_cancelled()
    {
        Touch("a.pdf", "b.pdf");
        var (sut, merge) = await BuildAsync();
        Select(sut, "a.pdf", "b.pdf");
        sut.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(null);

        await sut.MergeSelectedCommand.ExecuteAsync();

        Assert.Empty(merge.Merges);
    }

    [Fact]
    public async Task MergeAsync_surfaces_a_merge_failure_on_the_status_line_instead_of_throwing()
    {
        Touch("a.pdf", "b.pdf");
        var (sut, merge) = await BuildAsync();
        Select(sut, "a.pdf", "b.pdf");
        merge.ThrowOnMerge = new MyPersonalDrive.Services.Localization.LocalizedIOException(
            "boom",
            MyPersonalDrive.Services.Localization.LocalizedText.Verbatim("That file is not a readable PDF."));
        sut.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "joined.pdf"));

        await sut.MergeSelectedCommand.ExecuteAsync();

        Assert.Contains("not a readable PDF", sut.StatusMessage);
        Assert.False(sut.IsLoading);
    }

    /// <summary>Points the home directory at this test's temp root, so the pane never reads the real OS home. Mirrors the equivalent in <see cref="LocalExplorerViewModelTests"/>, which keeps its own private to that class.</summary>
    private sealed class FakeHomeLocalFileSystemService(string home) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;
    }
}
