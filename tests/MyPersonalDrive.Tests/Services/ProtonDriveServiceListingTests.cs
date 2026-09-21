using System.Globalization;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B2.1 (empty-vs-unparseable) and B2.3/B2.4 (typed ModifiedAt,
/// real field names instead of guessed aliases). Fixtures below use the actual JSON shape
/// verified against cli-drive@0.4.2 in docs/PLAN-LOCAL-SYNC.md Appendix A, not invented shapes.
/// </summary>
public class ProtonDriveServiceListingTests
{
    private const string RealFolderEntry = """
        {
          "uid": "rHChrZ...~parent==~ABC==",
          "parentUid": "rHChrZ...~ZWOKjf==",
          "name": { "ok": true, "value": "Photos" },
          "ownedBy": { "email": "ramiro.di.rico@proton.me" },
          "type": "folder",
          "mediaType": "Folder",
          "isShared": false,
          "isSharedPublicly": false,
          "creationTime": "2026-06-09T22:07:26.000Z",
          "modificationTime": "2026-06-09T22:07:26.000Z",
          "treeEventScopeId": "rHChrZ...~ABC=="
        }
        """;

    /// <summary>
    /// Verbatim from `filesystem list --json "/my-files/google-take-out-test-2/Graciosas"` on
    /// 2026-09-21, trimmed of the fields the parser does not read. Note `name` still carries the
    /// `{ ok, value }` envelope while `activeRevision` does not — the inconsistency is the CLI's,
    /// and is the whole point of this fixture.
    /// </summary>
    private const string RealFileEntryWithBareRevision = """
        {
          "uid": "rHChrZeeRKPwnTYTQdh7CGvVYHzjEQfiAiekIEALsvjpFk7A8s1GDE4s0WvHgHTYeG6yQvPmRhEZmu4t5P3Ftg==~BcZaKpNnxFjAIaboiRnIA2--Ei7kRJE5G3LArrsj2MAFsHykNJL-K52uVRYM1A8lTpTuglWnxbX8OZ62mKWhQg==",
          "name": { "ok": true, "value": "globant dealer.png" },
          "ownedBy": { "email": "ramiro.di.rico@proton.me" },
          "type": "file",
          "mediaType": "image/png",
          "isShared": false,
          "creationTime": "2026-09-21T15:36:01.000Z",
          "modificationTime": "2026-09-21T15:36:01.000Z",
          "totalStorageSize": 87029,
          "activeRevision": {
            "uid": "rHChrZ...~BcZaKp==~-WTlbS==",
            "state": "active",
            "creationTime": "2026-09-21T15:36:01.000Z",
            "storageSize": 87029,
            "isImported": false,
            "claimedSize": 49991,
            "claimedModificationTime": "2019-01-10T13:58:52.864Z",
            "claimedDigests": { "sha1": "8f246d6d4dd0f3a10137b4b0b931c63d152787df", "sha1Verified": false }
          }
        }
        """;

    private const string RealFileEntry = """
        {
          "uid": "rHChrZ...~file==~DEF==",
          "parentUid": "rHChrZ...~ZWOKjf==",
          "name": { "ok": true, "value": "10825139_1.pdf" },
          "ownedBy": { "email": "ramiro.di.rico@proton.me" },
          "type": "file",
          "mediaType": "application/pdf",
          "isShared": true,
          "isSharedPublicly": false,
          "creationTime": "2026-06-06T14:02:31.000Z",
          "modificationTime": "2026-06-06T14:02:46.000Z",
          "totalStorageSize": 6214012,
          "activeRevision": {
            "ok": true,
            "value": {
              "uid": "rHChrZ...~file==~rev==",
              "state": "active",
              "creationTime": "2026-06-06T14:02:31.000Z",
              "storageSize": 6214012,
              "claimedSize": 6196055,
              "claimedModificationTime": "2026-06-06T14:02:28.502Z",
              "claimedDigests": { "sha1": "a2abbf57e75de3b7da1312f64080090b5a0514f0", "sha1Verified": false }
            }
          },
          "treeEventScopeId": "rHChrZ...~ABC=="
        }
        """;

    [Fact]
    public async Task EmptyJsonArray_IsAnEmptyFolder_NotAParseFailure()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("[]");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files/empty");

        Assert.Empty(items);
    }

    [Fact]
    public async Task RealFolderEntry_IsParsedCorrectly()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{RealFolderEntry}]");
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("Photos", item.Name);
        Assert.True(item.IsFolder);
        Assert.Equal("/my-files/Photos", item.Path);
        Assert.Equal("ramiro.di.rico@proton.me", item.Owner);
        Assert.False(item.IsShared);
        Assert.StartsWith("rHChrZ", item.NodeId);
        Assert.Null(item.Size);
        // Folders fall back to the top-level modificationTime (see Appendix A #2).
        Assert.Equal(DateTimeOffset.Parse("2026-06-09T22:07:26.000Z", CultureInfo.InvariantCulture), item.ModifiedAt);
    }

    [Fact]
    public async Task RealFileEntry_ReadsSizeAndModifiedAtFromActiveRevision_NotTopLevel()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{RealFileEntry}]");
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("10825139_1.pdf", item.Name);
        Assert.False(item.IsFolder);
        Assert.True(item.IsShared);
        Assert.Equal("ramiro.di.rico@proton.me", item.Owner);

        // Size must come from activeRevision.claimedSize (the real local file size), never
        // totalStorageSize (the larger, encrypted on-server size).
        Assert.Equal(6196055, item.Size);

        // ModifiedAt must come from activeRevision.claimedModificationTime (the real local
        // mtime at upload time), never the top-level modificationTime (a server-side revision
        // event time, which in the source data used for this fixture was ~18s later).
        Assert.Equal(DateTimeOffset.Parse("2026-06-06T14:02:28.502Z", CultureInfo.InvariantCulture), item.ModifiedAt);

        Assert.Equal("a2abbf57e75de3b7da1312f64080090b5a0514f0", item.ContentHash);
        Assert.StartsWith("rHChrZ", item.NodeId);
    }

    /// <summary>
    /// Appendix A #1a. Captured from a real account on 2026-09-21: `activeRevision` came back as a
    /// bare object, with no `{ ok, value }` envelope around it — unlike #1's original capture, and
    /// unlike every other decryptable field, which still carry one (`name` in this very entry).
    ///
    /// The regression this pins is not a parse failure. `claimedSize`, `claimedModificationTime`
    /// and `claimedDigests.sha1` all live inside that object, so rejecting the bare shape yielded a
    /// perfectly well-formed item with no size, no hash, and an mtime silently fallen back to the
    /// server's revision time. The sync engine compares size first, so every file already uploaded
    /// measured as changed and was queued for upload again on every run — 513 of them, in the
    /// report that found this.
    /// </summary>
    [Fact]
    public async Task ABareActiveRevision_StillYieldsSizeHashAndTheClaimedMtime()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{RealFileEntryWithBareRevision}]");
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files/google-take-out-test-2/Graciosas"));

        Assert.Equal("globant dealer.png", item.Name);
        // claimedSize, not totalStorageSize (87029) — the plaintext size is what the local file
        // can be compared against.
        Assert.Equal(49991, item.Size);
        Assert.Equal("8f246d6d4dd0f3a10137b4b0b931c63d152787df", item.ContentHash);
        // The file's own mtime from 2019, not the 2026 upload time sitting in `modificationTime`.
        Assert.Equal(DateTimeOffset.Parse("2019-01-10T13:58:52.864Z", CultureInfo.InvariantCulture), item.ModifiedAt);
    }

    /// <summary>
    /// An `ok: false` envelope is a different thing from a missing one: it means the field could
    /// not be decrypted, so there is genuinely no revision to read. It must not be mistaken for a
    /// bare object and read field-by-field.
    /// </summary>
    [Fact]
    public async Task AnUndecryptableRevision_IsStillTreatedAsAbsent()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($$"""
            [{
              "uid": "rHChrZ...~file==~GHI==",
              "name": { "ok": true, "value": "unreadable.bin" },
              "type": "file",
              "modificationTime": "2026-09-21T15:36:01.000Z",
              "activeRevision": { "ok": false, "error": "could not decrypt" }
            }]
            """);
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("unreadable.bin", item.Name);
        Assert.Null(item.Size);
        Assert.Null(item.ContentHash);
    }

    [Fact]
    public async Task WrapperObjectShape_IsStillParsed_ThoughNeverObservedFromTheRealCli()
    {
        // Not observed in F0 testing (root is always a bare array for cli-drive@0.4.2) — kept
        // as defensive support in case a future CLI version wraps the array.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($$"""{ "items": [{{RealFolderEntry}}] }""");
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("Photos", item.Name);
    }

    [Fact]
    public async Task RootArray_WithMixedEntries_IsParsed()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput($"[{RealFolderEntry}, {RealFileEntry}]");
        var service = new ProtonDriveService(executor);

        var items = await service.LoadFolderAsync("/my-files");

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Name == "Photos" && i.IsFolder);
        Assert.Contains(items, i => i.Name == "10825139_1.pdf" && !i.IsFolder);
    }

    [Fact]
    public async Task UnrecognizedJsonShape_ThrowsInsteadOfReturningEmpty()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""{ "unexpectedField": "value" }""");
        var service = new ProtonDriveService(executor);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LoadFolderAsync("/my-files"));
    }

    [Fact]
    public async Task NonJsonOutput_FallsBackToTextParsing_ByDefault()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos\n📄 report.pdf");
        var service = new ProtonDriveService(executor, strictListingParsing: false);

        var items = await service.LoadFolderAsync("/my-files");

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Name == "Photos" && i.IsFolder);
        Assert.Contains(items, i => i.Name == "report.pdf" && !i.IsFolder);
    }

    [Fact]
    public async Task NonJsonOutput_ThrowsWhenStrictParsingIsEnabled()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos\n📄 report.pdf");
        var service = new ProtonDriveService(executor, strictListingParsing: true);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.LoadFolderAsync("/my-files"));
    }

    [Fact]
    public async Task NonJsonOutput_RaisesListingParseWarning()
    {
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("🗂 Photos");
        var service = new ProtonDriveService(executor);

        string? warning = null;
        service.ListingParseWarning += (_, message) => warning = message;

        await service.LoadFolderAsync("/my-files");

        Assert.NotNull(warning);
        Assert.Contains("/my-files", warning);
    }

    [Fact]
    public async Task NameDecryptionFailure_FallsBackToNodeUid_InsteadOfDroppingTheEntry()
    {
        // The CLI's own docs say a name can fail to decrypt; it falls back to the node uid.
        // Silently dropping such an entry would be the same "phantom delete" bug class B2.1
        // fixed for the whole-listing case, just at single-entry granularity.
        var executor = new FakeCliExecutor();
        executor.EnqueueOutput("""
            [
              {
                "uid": "rHChrZ...~undecryptable==",
                "parentUid": "rHChrZ...~ZWOKjf==",
                "name": { "ok": false },
                "type": "file",
                "isShared": false
              }
            ]
            """);
        var service = new ProtonDriveService(executor);

        var item = Assert.Single(await service.LoadFolderAsync("/my-files"));

        Assert.Equal("rHChrZ...~undecryptable==", item.Name);
    }
}
