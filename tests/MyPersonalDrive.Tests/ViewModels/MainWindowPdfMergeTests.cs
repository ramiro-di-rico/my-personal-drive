using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The cloud pane's PDF merge. Unlike the local pane's, this one has a round trip to prove: every
/// selected PDF comes down first, the merge runs on those temporary copies in the order the dialog
/// chose, and the result goes back up into the current folder. The merge itself is faked here —
/// <see cref="Services.PdfMergeServiceTests"/> covers the real one — so what these assert on is
/// the CLI calls the flow produced and the state it left behind.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowPdfMergeTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.PdfMerge").FullName;
    private readonly string? _originalAppData;

    public MainWindowPdfMergeTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DriveItem Item(string name)
        => new($"/my-files/{name}", name, IsFolder: false, Size: 100);

    private (MainWindowViewModel ViewModel, FakeCliExecutor Executor, FakePdfMergeService Merge) Build()
    {
        var executor = new FakeCliExecutor();
        var provider = new ProtonDriveProvider(new ProtonDriveService(executor));
        var store = new SyncStateStore(Path.Combine(_tempAppData, "sync.db"));
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var merge = new FakePdfMergeService();
        var viewModel = new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            pdfMerge: merge);
        return (viewModel, executor, merge);
    }

    /// <summary>
    /// Makes the fake CLI behave like a real download: writes a file into whatever folder the
    /// download argument named. Without this the flow would rightly report that the provider
    /// downloaded nothing.
    /// </summary>
    private static void RespondToDownloadsAndUploads(FakeCliExecutor executor, int count)
    {
        for (var i = 0; i < count + 4; i++)
        {
            executor.EnqueueOutput(args =>
            {
                if (args.Count >= 4 && args[0] == "filesystem" && args[1] == "download")
                {
                    var name = args[2].Split('/').Last();
                    File.WriteAllText(Path.Combine(args[3], name), "pdf bytes");
                }

                return string.Empty;
            });
        }
    }

    private static void SelectAll(MainWindowViewModel viewModel)
    {
        foreach (var node in viewModel.RootItems)
        {
            viewModel.ToggleSelection(node);
        }
    }

    [Fact]
    public void SelectedMergeableCount_counts_pdfs_and_images_and_gates_the_command()
    {
        var (viewModel, _, _) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.PDF"), Item("notes.txt")]);

        SelectAll(viewModel);

        Assert.Equal(3, viewModel.SelectedCount);
        Assert.Equal(2, viewModel.SelectedMergeableCount);
        Assert.True(viewModel.CanMergeSelected);
        Assert.True(viewModel.MergeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedMergeableCount_counts_images_alongside_pdfs()
    {
        var (viewModel, _, _) = Build();
        viewModel.DisplayItems([Item("scan.pdf"), Item("photo.jpg"), Item("logo.png"), Item("notes.txt"), Item("raw.cr2")]);

        SelectAll(viewModel);

        // The PDF and the three-format image set count; the text file does not, and neither does a
        // RAW file — the merge accepts exactly what the image viewer can decode, no wider.
        Assert.Equal(3, viewModel.SelectedMergeableCount);
        Assert.True(viewModel.CanMergeSelected);
    }

    [Fact]
    public async Task MergeAsync_works_with_images_only_no_pdf_required()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("one.jpg"), Item("two.png")]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 2);
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "album.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        // Two images and no PDF at all is a valid merge: the result is still a PDF.
        var (sources, output) = Assert.Single(merge.Merges);
        Assert.Equal(2, sources.Count);
        Assert.EndsWith("album.pdf", output, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeCommand_stays_disabled_with_only_one_pdf_selected()
    {
        var (viewModel, _, _) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("notes.txt")]);

        SelectAll(viewModel);

        Assert.Equal(1, viewModel.SelectedMergeableCount);
        Assert.False(viewModel.CanMergeSelected);
        Assert.False(viewModel.MergeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeAsync_downloads_every_source_then_uploads_the_result()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.pdf")]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 2);
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "joined.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        var downloads = executor.Calls.Where(c => c.Arguments is ["filesystem", "download", ..]).ToList();
        Assert.Equal(["/my-files/a.pdf", "/my-files/b.pdf"], downloads.Select(c => c.Arguments[2]));

        var upload = Assert.Single(executor.Calls, c => c.Arguments is ["filesystem", "upload", ..]);
        Assert.Contains("joined.pdf", string.Join(" ", upload.Arguments), StringComparison.Ordinal);
        Assert.Single(merge.Merges);
    }

    [Fact]
    public async Task MergeAsync_downloads_each_source_into_its_own_folder_so_equal_names_cannot_collide()
    {
        var (viewModel, executor, merge) = Build();
        // Two different remote files that happen to share a name — the case a single shared temp
        // folder would silently turn into "merge a.pdf with itself".
        viewModel.DisplayItems(
        [
            new DriveItem("/my-files/one/a.pdf", "a.pdf", IsFolder: false, Size: 100),
            new DriveItem("/my-files/two/a.pdf", "a.pdf", IsFolder: false, Size: 100),
        ]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 2);
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "joined.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        var destinations = executor.Calls
            .Where(c => c.Arguments is ["filesystem", "download", ..])
            .Select(c => c.Arguments[3])
            .ToList();
        Assert.Equal(2, destinations.Distinct().Count());

        var (sources, _) = Assert.Single(merge.Merges);
        Assert.Equal(2, sources.Distinct().Count());
    }

    [Fact]
    public async Task MergeAsync_merges_in_the_order_the_dialog_returned()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.pdf"), Item("c.pdf")]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 3);
        // Reordered to c, a — and b dropped.
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([2, 0], "joined.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        var downloads = executor.Calls
            .Where(c => c.Arguments is ["filesystem", "download", ..])
            .Select(c => c.Arguments[2])
            .ToList();
        Assert.Equal(["/my-files/c.pdf", "/my-files/a.pdf"], downloads);

        var (sources, _) = Assert.Single(merge.Merges);
        Assert.EndsWith("c.pdf", sources[0], StringComparison.Ordinal);
        Assert.EndsWith("a.pdf", sources[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeAsync_does_not_touch_the_provider_when_the_dialog_is_cancelled()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.pdf")]);
        SelectAll(viewModel);
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(null);

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        Assert.Empty(merge.Merges);
        Assert.DoesNotContain(executor.Calls, c => c.Arguments is ["filesystem", "download", ..]);
    }

    [Fact]
    public async Task MergeAsync_surfaces_a_merge_failure_instead_of_throwing_out_of_the_command()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.pdf")]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 2);
        merge.ThrowOnMerge = new MyPersonalDrive.Services.Localization.LocalizedIOException(
            "boom",
            MyPersonalDrive.Services.Localization.LocalizedText.Verbatim("That file is not a readable PDF."));
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "joined.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        // Downloaded, failed at the merge, and never uploaded a thing.
        Assert.DoesNotContain(executor.Calls, c => c.Arguments is ["filesystem", "upload", ..]);
        Assert.False(viewModel.IsLoading);
        Assert.NotNull(viewModel.StatusMessage);
    }

    [Fact]
    public async Task MergeAsync_deletes_its_temporary_copies_whether_it_succeeds_or_fails()
    {
        var (viewModel, executor, merge) = Build();
        viewModel.DisplayItems([Item("a.pdf"), Item("b.pdf")]);
        SelectAll(viewModel);
        RespondToDownloadsAndUploads(executor, 2);
        viewModel.RequestPdfMergeAsync = (_, _) => Task.FromResult<PdfMergeRequest?>(new PdfMergeRequest([0, 1], "joined.pdf"));

        await viewModel.MergeSelectedCommand.ExecuteAsync();

        var (sources, output) = Assert.Single(merge.Merges);
        // Every temp copy the merge was handed, and the output beside them, are gone afterwards —
        // these are whole PDFs on disk, not a few bytes of cache.
        Assert.All(sources, path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(output));
    }
}
