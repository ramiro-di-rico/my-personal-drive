using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class RemoteScannerTests
{
    private static string FileJson(string uid, string name, long claimedSize, string modifiedAt, string sha1)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "file", "isShared": false,
              "modificationTime": "{{modifiedAt}}",
              "activeRevision": {
                "ok": true,
                "value": {
                  "claimedSize": {{claimedSize}},
                  "claimedModificationTime": "{{modifiedAt}}",
                  "claimedDigests": { "sha1": "{{sha1}}" }
                }
              }
            }
            """;

    private static string FolderJson(string uid, string name)
        => $$"""
            {
              "uid": "{{uid}}", "parentUid": "parent",
              "name": { "ok": true, "value": "{{name}}" },
              "ownedBy": { "email": "ramiro.di.rico@proton.me" },
              "type": "folder", "isShared": false,
              "modificationTime": "2026-01-01T00:00:00.000Z"
            }
            """;

    [Fact]
    public async Task FlatFolder_ReturnsAllChildren()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FileJson("u1", "a.txt", 10, "2026-01-01T00:00:00.000Z", "hash-a")}]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        var result = await scanner.ScanAsync("/my-files/Docs", mapper, new ExclusionMatcher());

        var entry = Assert.Single(result).Value;
        Assert.Equal("a.txt", entry.RelativePath);
        Assert.Equal(10, entry.Size);
        Assert.Equal("hash-a", entry.ContentHash);
        Assert.Equal("u1", entry.NodeId);
    }

    [Fact]
    public async Task NestedFolders_AreWalked_BreadthFirst()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FolderJson("u-sub", "sub")}]");
        executor.RespondForPath("/my-files/Docs/sub", $"[{FileJson("u-file", "b.txt", 5, "2026-01-01T00:00:00.000Z", "hash-b")}]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        var result = await scanner.ScanAsync("/my-files/Docs", mapper, new ExclusionMatcher());

        Assert.Equal(2, result.Count);
        Assert.True(result["sub"].IsFolder);
        Assert.False(result["sub/b.txt"].IsFolder);
        Assert.Equal("hash-b", result["sub/b.txt"].ContentHash);
    }

    [Fact]
    public async Task ExcludedFolder_IsNotRecursedInto()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FolderJson("u-git", ".git")}, {FileJson("u-file", "real.txt", 1, "2026-01-01T00:00:00.000Z", "hash")}]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        var result = await scanner.ScanAsync("/my-files/Docs", mapper, new ExclusionMatcher());

        Assert.DoesNotContain(".git", result.Keys);
        Assert.Contains("real.txt", result.Keys);
        // If it had recursed into .git, this path would exist and the call would have thrown
        // (no response configured for it) — reaching this point without an exception is itself
        // part of the assertion.
    }

    [Fact]
    public async Task EmptyFolder_ReturnsEmptyResult()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", "[]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        var result = await scanner.ScanAsync("/my-files/Docs", mapper, new ExclusionMatcher());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ConcurrencyIsBounded_NeverExceedsMaxConcurrentCalls()
    {
        const int maxConcurrency = 2;
        var executor = new ConcurrencyTrackingExecutor(maxConcurrency);
        // Fan out: root has 6 subfolders, each with one file — enough waves to exercise the bound.
        var rootChildren = string.Join(',', Enumerable.Range(0, 6).Select(i => FolderJson($"u{i}", $"f{i}")));
        executor.RespondForPath("/my-files/Docs", $"[{rootChildren}]");
        for (var i = 0; i < 6; i++)
        {
            executor.RespondForPath($"/my-files/Docs/f{i}", $"[{FileJson($"uf{i}", "leaf.txt", 1, "2026-01-01T00:00:00.000Z", "hash")}]");
        }

        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider, maxConcurrency);
        var mapper = new PathMapper("/my-files/Docs", "/home/user/Docs");

        await scanner.ScanAsync("/my-files/Docs", mapper, new ExclusionMatcher());

        Assert.False(executor.ExceededBound);
    }

    /// <summary>Tracks concurrent in-flight calls to verify RemoteScanner's semaphore actually bounds them.</summary>
    private sealed class ConcurrencyTrackingExecutor : IProtonDriveCliExecutor
    {
        private readonly int _maxConcurrency;
        private readonly Dictionary<string, string> _responses = new();
        private int _current;

        public ConcurrencyTrackingExecutor(int maxConcurrency) => _maxConcurrency = maxConcurrency;

        public bool ExceededBound { get; private set; }

        public event EventHandler<CliCommandStartedEventArgs>? CommandStarted;
        public event EventHandler<CliCommandOutputEventArgs>? CommandOutput;
        public event EventHandler<CliCommandFinishedEventArgs>? CommandFinished;

        public void RespondForPath(string path, string stdout) => _responses[path] = stdout;

        public Task ResetRemoteCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        {
            var current = Interlocked.Increment(ref _current);
            if (current > _maxConcurrency)
            {
                ExceededBound = true;
            }

            await Task.Delay(5, cancellationToken); // give overlapping calls a chance to race
            Interlocked.Decrement(ref _current);

            return _responses[arguments[^1]];
        }
    }

    // ---- node names that can't exist locally (backlog B1) ----

    [Fact]
    public async Task ANodeWhoseNameContainsASlash_IsSkippedAndReported()
    {
        // '/' is the one byte a Linux filename may not contain, so this node can't be mirrored under
        // its real name. Skipping keeps relative paths unambiguously '/'-separated; the alternative
        // would be inventing a substitute name that a TwoWay pair would upload back as a second copy.
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FileJson("uid-ok", "ok.txt", 3, "2026-01-01T00:00:00.000Z", "hash-ok")}, {FileJson("uid-slash", "in/voice.pdf", 3, "2026-01-01T00:00:00.000Z", "hash-slash")}]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);

        var skipped = new List<string>();
        scanner.NodeSkipped += (_, name) => skipped.Add(name);

        var result = await scanner.ScanAsync("/my-files/Docs", new PathMapper("/my-files/Docs", "/tmp/x"), new ExclusionMatcher([]));

        Assert.Equal(["ok.txt"], result.Keys);
        Assert.Equal(["in/voice.pdf"], skipped);
    }

    [Fact]
    public async Task AFolderWithAnUnmappableName_IsNotDescendedInto()
    {
        // Its children can't be represented locally either, and listing them would cost a CLI call
        // per folder to produce paths that would then have to be thrown away.
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FolderJson("uid-ab", "a/b")}]");
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var scanner = new RemoteScanner(provider);

        var result = await scanner.ScanAsync("/my-files/Docs", new PathMapper("/my-files/Docs", "/tmp/x"), new ExclusionMatcher([]));

        Assert.Empty(result);
        Assert.Single(executor.Calls); // only the root was listed
    }

    // ---- case-insensitive providers (docs/PLAN-CLOUD-PROVIDERS.md §2.4, P3) ----
    // Proton itself is always case-sensitive; these exercise the generic detection through a
    // decorator that only overrides Paths.Comparison, since no case-insensitive provider exists yet.

    [Fact]
    public async Task OnACaseInsensitiveProvider_TwoNamesDifferingOnlyByCase_AreBothSkippedAndReported()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs",
            $"[{FileJson("uid-1", "Report.txt", 3, "2026-01-01T00:00:00.000Z", "hash-1")}, " +
            $"{FileJson("uid-2", "report.txt", 4, "2026-01-01T00:00:00.000Z", "hash-2")}]");
        var provider = new CaseInsensitivePathsDecorator(new ProtonDriveProvider(new ProtonDriveService(executor)));
        var scanner = new RemoteScanner(provider);

        var skipped = new List<string>();
        scanner.NodeSkipped += (_, name) => skipped.Add(name);

        var result = await scanner.ScanAsync("/my-files/Docs", new PathMapper("/my-files/Docs", "/tmp/x"), new ExclusionMatcher([]));

        Assert.Empty(result);
        Assert.Equal(new HashSet<string> { "Report.txt", "report.txt" }, skipped.ToHashSet());
    }

    [Fact]
    public async Task OnACaseInsensitiveProvider_NamesDifferingByMoreThanCase_AreNotTreatedAsCollisions()
    {
        var executor = new FakeCliExecutor();
        executor.RespondForPath("/my-files/Docs", $"[{FileJson("uid-1", "a.txt", 3, "2026-01-01T00:00:00.000Z", "hash-1")}]");
        var provider = new CaseInsensitivePathsDecorator(new ProtonDriveProvider(new ProtonDriveService(executor)));
        var scanner = new RemoteScanner(provider);

        var result = await scanner.ScanAsync("/my-files/Docs", new PathMapper("/my-files/Docs", "/tmp/x"), new ExclusionMatcher([]));

        Assert.Equal(["a.txt"], result.Keys);
    }

    /// <summary>Wraps a real provider, overriding only <see cref="IProviderPathSyntax.Comparison"/>.</summary>
    private sealed class CaseInsensitivePathsDecorator : ICloudDriveProvider
    {
        private readonly ICloudDriveProvider _inner;

        public CaseInsensitivePathsDecorator(ICloudDriveProvider inner) => _inner = inner;

        public ProviderId Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public ProviderCapabilities Capabilities => _inner.Capabilities;
        public IDriveOperations Operations => _inner.Operations;
        public IDriveAuthenticator Auth => _inner.Auth;
        public IProviderPathSyntax Paths { get; } = new FakePathSyntax(StringComparison.OrdinalIgnoreCase);
        public IRemoteViewInvalidator? RemoteView => _inner.RemoteView;
        public IProviderDiagnostics? Diagnostics => _inner.Diagnostics;
        public event EventHandler<ProviderActivity>? Activity { add => _inner.Activity += value; remove => _inner.Activity -= value; }
        public event EventHandler<string>? ListingParseWarning { add => _inner.ListingParseWarning += value; remove => _inner.ListingParseWarning -= value; }
    }

    private sealed class FakePathSyntax : IProviderPathSyntax
    {
        public FakePathSyntax(StringComparison comparison) => Comparison = comparison;
        public StringComparison Comparison { get; }
        public string Combine(string parentPath, string name) => ProtonDriveService.CombinePath(parentPath, name);
        public bool IsRemoteNameMappableLocally(string name) => !ProtonDriveService.HasUnmappableName(name);
    }
}
