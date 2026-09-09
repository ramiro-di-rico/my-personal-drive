using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The <see cref="SyncPair.SharesLocalFolder"/> flag, which is the piece that connects the two
/// halves of shared-folder support: <see cref="SyncPairValidator"/> decides which combinations may
/// share, and this flag is what then tells <c>SyncExecutor</c> to keep a baseline and
/// <c>SyncReconciler</c> to protect foreign files. Nothing else derives it at sync time — the
/// executor only ever sees one account's store, and a shared folder is shared across accounts —
/// so if the panel fails to maintain it, the protection silently never runs.
///
/// <see cref="SyncPairValidatorTests"/> covers the rules and <see cref="SyncReconcilerTests"/> the
/// protection; what's proven here is that the flag actually gets set and cleared.
/// </summary>
public class SyncSharedLocalFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mypersonaldrive-shared-folder").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-shared-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private (SyncPanelViewModel Panel, SyncStateStore Store) Build()
    {
        var store = new SyncStateStore(_dbPath);
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        return (new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store)), store);
    }

    /// <summary>An additive download pair — the shape allowed to share a folder with another download pair.</summary>
    private static void AnswerAdditiveDownload(SyncPanelViewModel panel, string remotePath, string localPath)
        => panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest(remotePath, localPath, SyncDirection.RemoteToLocal, ConflictPolicy.Ask, MirrorDeletes: false));

    [Fact]
    public async Task ASinglePair_DoesNotClaimToShareItsFolder()
    {
        var (panel, store) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/Docs", folder);
        await panel.AddPairCommand.ExecuteAsync();

        var pair = Assert.Single(await store.GetPairsAsync());
        Assert.False(pair.SharesLocalFolder);
    }

    [Fact]
    public async Task AddingASecondPairOnTheSameFolder_MarksBothOfThem()
    {
        var (panel, store) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/Docs", folder);
        await panel.AddPairCommand.ExecuteAsync();
        AnswerAdditiveDownload(panel, "/my-files/Other", folder);
        await panel.AddPairCommand.ExecuteAsync();

        // Both, not just the newcomer: the pair that was there first is equally unable to tell the
        // other one's files from stale ones, so it needs the same baseline and the same protection.
        var pairs = await store.GetPairsAsync();
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.SharesLocalFolder));
    }

    [Fact]
    public async Task TheRowTheUserSeesCarriesTheFlagImmediately_NotOnlyAfterAReload()
    {
        // The flag decides how the engine treats the pair from its very first run, so the freshly
        // added row has to be the re-read one, not the pre-flag object the insert returned.
        var (panel, _) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/Docs", folder);
        await panel.AddPairCommand.ExecuteAsync();
        AnswerAdditiveDownload(panel, "/my-files/Other", folder);
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Equal(2, panel.Pairs.Count);
        Assert.All(panel.Pairs, row => Assert.True(row.SharesLocalFolder));
    }

    [Fact]
    public async Task PairsOnDifferentFolders_AreNotMarked()
    {
        var (panel, store) = Build();
        var first = Path.Combine(_root, "A");
        var second = Path.Combine(_root, "B");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/A", first);
        await panel.AddPairCommand.ExecuteAsync();
        AnswerAdditiveDownload(panel, "/my-files/B", second);
        await panel.AddPairCommand.ExecuteAsync();

        Assert.All(await store.GetPairsAsync(), p => Assert.False(p.SharesLocalFolder));
    }

    [Fact]
    public async Task DifferentSpellingsOfOneFolder_StillCountAsShared()
    {
        // The flag has to use the validator's own normalization, or a pair added as "Docs/" would
        // share the folder in practice while both pairs believe they own it alone.
        var (panel, store) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/Docs", folder);
        await panel.AddPairCommand.ExecuteAsync();
        AnswerAdditiveDownload(panel, "/my-files/Other", folder + Path.DirectorySeparatorChar);
        await panel.AddPairCommand.ExecuteAsync();

        var pairs = await store.GetPairsAsync();
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.SharesLocalFolder));
    }

    [Fact]
    public async Task RemovingOneOfTwoSharingPairs_ClearsTheFlagOnTheSurvivor()
    {
        // Otherwise the survivor keeps a baseline and keeps raising conflicts about files nothing
        // else is putting there any more.
        var (panel, store) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        AnswerAdditiveDownload(panel, "/my-files/Docs", folder);
        await panel.AddPairCommand.ExecuteAsync();
        AnswerAdditiveDownload(panel, "/my-files/Other", folder);
        await panel.AddPairCommand.ExecuteAsync();

        var doomed = panel.Pairs.First(p => p.RemotePath == "/my-files/Other");
        await doomed.RemoveCommand.ExecuteAsync();

        var survivor = Assert.Single(await store.GetPairsAsync());
        Assert.Equal("/my-files/Docs", survivor.RemotePath);
        Assert.False(survivor.SharesLocalFolder);
    }

    [Fact]
    public async Task WithThreePairsSharing_RemovingOneLeavesTheOtherTwoMarked()
    {
        var (panel, store) = Build();
        var folder = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(folder);
        await panel.InitializeAsync();

        foreach (var remote in new[] { "/my-files/A", "/my-files/B", "/my-files/C" })
        {
            AnswerAdditiveDownload(panel, remote, folder);
            await panel.AddPairCommand.ExecuteAsync();
        }

        await panel.Pairs.First(p => p.RemotePath == "/my-files/C").RemoveCommand.ExecuteAsync();

        var pairs = await store.GetPairsAsync();
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.SharesLocalFolder));
    }
}
