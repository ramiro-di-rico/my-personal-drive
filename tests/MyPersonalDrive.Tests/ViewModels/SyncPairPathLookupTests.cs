using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// §12 (docs/PLAN-UX-ROUND-2.md): the properties dialog answers "is this synced, and to where" for
/// any item inside a pair, not just the pair root. <c>FindPairByRemotePath</c> matches the root
/// exactly, which is all the row badges ever needed.
/// </summary>
public class SyncPairPathLookupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-pathlookup-{Guid.NewGuid():N}.db");
    private readonly string _libros = Directory.CreateTempSubdirectory("mypersonaldrive-libros").FullName;
    private readonly string _libros2 = Directory.CreateTempSubdirectory("mypersonaldrive-libros2").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
            Directory.Delete(_libros, recursive: true);
            Directory.Delete(_libros2, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<SyncPanelViewModel> BuildAsync()
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath("/", "[]");
        var provider = new ProtonDriveProvider(new ProtonDriveService(cli));
        var store = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));

        await store.CreatePairAsync("/my-files/Libros", _libros, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await store.CreatePairAsync("/my-files/Libros2", _libros2, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        await panel.InitializeAsync();
        return panel;
    }

    private async Task<MyPersonalDrive.ViewModels.MainWindowViewModel> BuildViewModelAsync()
    {
        var panel = await BuildAsync();
        var cli = new FakeCliExecutor();
        var provider = new ProtonDriveProvider(new ProtonDriveService(cli));
        var cacheDir = Directory.CreateTempSubdirectory("mypersonaldrive-pathlookup-cache").FullName;
        return new MyPersonalDrive.ViewModels.MainWindowViewModel(
            provider,
            new MyPersonalDrive.Services.DriveCacheService(Path.Combine(cacheDir, "cache.db")),
            new MyPersonalDrive.Services.AppSettingsService(),
            panel);
    }

    [Fact]
    public async Task APairRoot_IsFoundByItself()
    {
        var panel = await BuildAsync();

        var pair = panel.FindPairContainingRemotePath("/my-files/Libros");

        Assert.NotNull(pair);
        Assert.Equal("/my-files/Libros", pair!.RemotePath);
    }

    [Fact]
    public async Task AFileSeveralFoldersDeep_IsFoundInsideItsPair()
    {
        var panel = await BuildAsync();

        var pair = panel.FindPairContainingRemotePath("/my-files/Libros/tecnicos/2026/refactoring.pdf");

        Assert.NotNull(pair);
        Assert.Equal("/my-files/Libros", pair!.RemotePath);
    }

    // The reason the check is segment-wise and not a bare StartsWith.
    [Fact]
    public async Task ASiblingWhoseNameSharesThePrefix_IsNotTreatedAsBeingInside()
    {
        var panel = await BuildAsync();

        var pair = panel.FindPairContainingRemotePath("/my-files/Libros2/otro.pdf");

        Assert.NotNull(pair);
        Assert.Equal("/my-files/Libros2", pair!.RemotePath);
    }

    [Fact]
    public async Task APathOutsideEveryPair_FindsNothing()
    {
        var panel = await BuildAsync();

        Assert.Null(panel.FindPairContainingRemotePath("/my-files/Fotos/vacaciones.jpg"));
        Assert.Null(panel.FindPairContainingRemotePath("/my-files"));
    }

    [Fact]
    public async Task TheLocalSide_AnswersTheSameQuestionInReverse()
    {
        var panel = await BuildAsync();

        var pair = panel.FindPairContainingLocalPath(Path.Combine(_libros, "tecnicos", "refactoring.pdf"));

        Assert.NotNull(pair);
        Assert.Equal(_libros, pair!.LocalPath);
        Assert.Null(panel.FindPairContainingLocalPath("/home/ramiro/Documents/otro.pdf"));
    }

    // End to end: the field actually reaches the dialog, with a copy button, for an item inside a
    // pair — and is absent for one outside every pair.
    [Fact]
    public async Task ThePropertiesDialog_GetsTheLocalPath_ForAnItemInsideAPair()
    {
        var vm = await BuildViewModelAsync();
        IReadOnlyList<PropertyField> shown = [];
        vm.RequestShowPropertiesAsync = (_, fields) => { shown = fields; return Task.CompletedTask; };

        await vm.ShowPropertiesAsync(new DriveItem("/my-files/Libros/tecnicos/refactoring.pdf", "refactoring.pdf", IsFolder: false));

        var local = Assert.Single(shown, field => field.Label == "Local path");
        Assert.Equal(Path.Combine(_libros, "tecnicos", "refactoring.pdf"), local.Value);
        Assert.True(local.IsCopyable);

        // The remote path is copyable too — both are paths, and offering to copy only one of them
        // would be arbitrary.
        Assert.True(Assert.Single(shown, field => field.Label == "Path").IsCopyable);
    }

    [Fact]
    public async Task ThePropertiesDialog_OmitsTheLocalPath_ForAnItemOutsideEveryPair()
    {
        var vm = await BuildViewModelAsync();
        IReadOnlyList<PropertyField> shown = [];
        vm.RequestShowPropertiesAsync = (_, fields) => { shown = fields; return Task.CompletedTask; };

        await vm.ShowPropertiesAsync(new DriveItem("/my-files/Fotos/vacaciones.jpg", "vacaciones.jpg", IsFolder: false));

        Assert.DoesNotContain(shown, field => field.Label == "Local path");
    }

    // The mapping itself goes through PathMapper, so the dialog cannot disagree with the path the
    // sync engine actually writes to (docs/PLAN-LOCAL-SYNC.md §3.2's golden rule).
    [Fact]
    public async Task TheMappedLocalPath_IsTheOneTheSyncEngineWouldUse()
    {
        var panel = await BuildAsync();
        var pair = panel.FindPairContainingRemotePath("/my-files/Libros/tecnicos/refactoring.pdf")!;

        var mapper = new PathMapper(pair.RemotePath, pair.LocalPath);
        var relative = mapper.ToRelativeFromRemote("/my-files/Libros/tecnicos/refactoring.pdf");

        Assert.Equal("tecnicos/refactoring.pdf", relative);
        Assert.Equal(
            Path.Combine(_libros, "tecnicos", "refactoring.pdf"),
            mapper.ToLocalAbsolute(relative));
    }
}
