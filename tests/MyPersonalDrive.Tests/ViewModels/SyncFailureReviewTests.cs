using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// U6 (docs/PLAN-UX-ROUND-2.md §6): the per-action failure review. The detail was always in the
/// durable queue — <see cref="QueuedSyncAction"/> carries a path, an operation and the provider's
/// own error per row — and the panel collapsed it to a count, then asked the user to retry blind.
/// Same seam as <c>SyncConflictFlowTests</c>: the dialog is replaced by a function returning
/// decisions.
/// </summary>
public class SyncFailureReviewTests : IDisposable
{
    private readonly string _localRoot = Directory.CreateTempSubdirectory("mypersonaldrive-failure-review").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-failure-review-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_localRoot, recursive: true);
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Harness(SyncPairViewModel Row, SyncStateStore Store, SyncPair Pair);

    private async Task<Harness> BuildAsync(params (string Path, string Error)[] failures)
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath("/", "[]");
        var provider = new ProtonDriveProvider(new ProtonDriveService(cli));
        var store = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));

        var pair = await store.CreatePairAsync("/my-files/Docs", _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var actions = failures
            .Select(f => new SyncAction(SyncOperation.DownloadFile, f.Path, null, 100, 0))
            .ToList();
        await store.EnqueueActionsAsync(pair.Id, actions, DateTimeOffset.UtcNow);

        // Fail each queued row with a distinct provider message, the way SyncExecutor would.
        var queued = await store.GetPendingActionsAsync(pair.Id);
        foreach (var (row, failure) in queued.Zip(failures))
        {
            await store.MarkFailedAsync(row.Id, failure.Error, null);
        }

        await store.UpdatePairStatusAsync(pair.Id, DateTimeOffset.UtcNow, SyncPairStatus.PartialFailure, $"{failures.Length} acción(es) fallaron");

        var row2 = new SyncPairViewModel(await store.GetPairAsync(pair.Id) ?? pair, executor, store, _ => { });
        await row2.RefreshOutstandingAsync();
        return new Harness(row2, store, pair);
    }

    [Fact]
    public async Task TheReview_ShowsThePathOperationAndTheProvidersOwnReason_PerAction()
    {
        var h = await BuildAsync(
            ("informe.pdf", "insufficient disk space"),
            ("notas.txt", "permission denied"));

        IReadOnlyList<SyncFailureViewModel> shown = [];
        h.Row.RequestFailureReviewAsync = failures =>
        {
            shown = failures;
            return Task.FromResult<IReadOnlyDictionary<long, SyncFailureDecision>>(
                new Dictionary<long, SyncFailureDecision>());
        };

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        Assert.Equal(2, shown.Count);
        var informe = shown.Single(f => f.RelativePath == "informe.pdf");
        Assert.Equal("Descargar", informe.OperationText);
        // Verbatim: paraphrasing the provider's sentence is how the actionable detail gets lost.
        Assert.Equal("insufficient disk space", informe.ReasonText);
        Assert.Contains("permission denied", shown.Single(f => f.RelativePath == "notas.txt").ReasonText);
    }

    [Fact]
    public async Task DismissingTheDialog_ChangesNothing()
    {
        var h = await BuildAsync(("informe.pdf", "insufficient disk space"));
        h.Row.RequestFailureReviewAsync = _ => Task.FromResult<IReadOnlyDictionary<long, SyncFailureDecision>>(
            new Dictionary<long, SyncFailureDecision>());

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        Assert.Equal(1, h.Row.FailedCount);
        Assert.True(h.Row.HasFailures);
        Assert.Single(await h.Store.GetFailedActionsAsync(h.Pair.Id));
    }

    [Fact]
    public async Task RetryingOneAndDiscardingTheOther_AppliesEachDecisionIndependently()
    {
        var h = await BuildAsync(
            ("informe.pdf", "insufficient disk space"),
            ("notas.txt", "permission denied"));

        IReadOnlyList<SyncFailureViewModel> shown = [];
        h.Row.RequestFailureReviewAsync = failures =>
        {
            shown = failures;
            return Task.FromResult<IReadOnlyDictionary<long, SyncFailureDecision>>(new Dictionary<long, SyncFailureDecision>
            {
                [failures.Single(f => f.RelativePath == "informe.pdf").Id] = SyncFailureDecision.Retry,
                [failures.Single(f => f.RelativePath == "notas.txt").Id] = SyncFailureDecision.Discard,
            });
        };

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        // The retried one is back to Pending with a clean slate; the discarded one is gone for good.
        Assert.Equal(0, h.Row.FailedCount);
        Assert.Empty(await h.Store.GetFailedActionsAsync(h.Pair.Id));

        var pending = await h.Store.GetPendingActionsAsync(h.Pair.Id);
        var revived = Assert.Single(pending);
        Assert.Equal("informe.pdf", revived.RelativePath);
        Assert.Equal(0, revived.AttemptCount);
        Assert.Null(revived.LastError);
    }

    // Discard deletes the row rather than marking it Done: the action never happened, and recording
    // it as completed would corrupt the baseline the next scan compares against.
    [Fact]
    public async Task Discarding_RemovesTheRowEntirely_RatherThanRecordingItAsDone()
    {
        var h = await BuildAsync(("notas.txt", "permission denied"));
        h.Row.RequestFailureReviewAsync = failures => Task.FromResult<IReadOnlyDictionary<long, SyncFailureDecision>>(
            new Dictionary<long, SyncFailureDecision> { [failures[0].Id] = SyncFailureDecision.Discard });

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        Assert.Empty(await h.Store.GetFailedActionsAsync(h.Pair.Id));
        Assert.Empty(await h.Store.GetPendingActionsAsync(h.Pair.Id));
    }

    // A half-made decision must not let the row claim it is healthy again.
    [Fact]
    public async Task DecidingOnlySomeActions_LeavesThePairStillFlaggedAsFailing()
    {
        var h = await BuildAsync(
            ("informe.pdf", "insufficient disk space"),
            ("notas.txt", "permission denied"));

        h.Row.RequestFailureReviewAsync = failures => Task.FromResult<IReadOnlyDictionary<long, SyncFailureDecision>>(
            new Dictionary<long, SyncFailureDecision>
            {
                [failures.Single(f => f.RelativePath == "notas.txt").Id] = SyncFailureDecision.Discard,
            });

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        Assert.Equal(1, h.Row.FailedCount);
        Assert.True(h.Row.HasFailures);
    }

    // The one-click path the row always had, kept alongside the per-action one.
    [Fact]
    public async Task RetryFailed_StillRequeuesEverythingWithoutOpeningTheReview()
    {
        var h = await BuildAsync(
            ("informe.pdf", "insufficient disk space"),
            ("notas.txt", "permission denied"));

        await h.Row.RetryFailedCommand.ExecuteAsync();

        Assert.Equal(0, h.Row.FailedCount);
        Assert.Equal(2, (await h.Store.GetPendingActionsAsync(h.Pair.Id)).Count);
    }

    [Fact]
    public async Task WithNoHandlerAttached_TheReviewSaysSoInsteadOfThrowing()
    {
        var h = await BuildAsync(("notas.txt", "permission denied"));
        h.Row.RequestFailureReviewAsync = null;

        await h.Row.ReviewFailuresCommand.ExecuteAsync();

        Assert.Contains("no está disponible", h.Row.StatusText);
        Assert.Equal(1, h.Row.FailedCount);
    }

    /// <summary>
    /// The regression U4 exposed: <c>HasFailures</c> used to substring-match the pair's LastError
    /// for failure wording, so translating <c>SyncExecutor.BuildStatusMessage</c> silently changed
    /// which pairs looked broken. It now reads the durable queue, which is the only thing that
    /// actually knows (docs/PLAN-UX-ROUND-2.md §6).
    /// </summary>
    [Fact]
    public async Task HasFailures_IgnoresTheWordingOfLastError_AndReadsTheQueue()
    {
        var h = await BuildAsync(("notas.txt", "permission denied"));

        // Rewrite the status message to something with none of the words the old check looked for.
        await h.Store.UpdatePairStatusAsync(h.Pair.Id, DateTimeOffset.UtcNow, SyncPairStatus.PartialFailure, "algo salió mal");
        await h.Row.RefreshOutstandingAsync();

        Assert.True(h.Row.HasFailures);

        // And with the queue drained, the same message must not keep the row looking broken.
        await h.Store.DiscardFailedAsync(h.Pair.Id, (await h.Store.GetFailedActionsAsync(h.Pair.Id)).Select(a => a.Id).ToList());
        await h.Store.UpdatePairStatusAsync(h.Pair.Id, DateTimeOffset.UtcNow, SyncPairStatus.Ok, null);
        await h.Row.RefreshOutstandingAsync();

        Assert.False(h.Row.HasFailures);
    }
}
