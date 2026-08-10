using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using Xunit;
using Xunit.Abstractions;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// The end-to-end check behind the parallel-scan change: a real <see cref="RemoteScanner"/> walking
/// a real remote tree, once serialized and once concurrent, against the live CLI.
///
/// <b>Read-only by construction.</b> Unlike the other real-CLI classes this one creates nothing and
/// deletes nothing — it only lists an existing folder, so it can be pointed at real data. The folder
/// comes from <c>MYPERSONALDRIVE_SCAN_ROOT</c> and defaults to <c>/my-files</c>.
///
/// Two properties are asserted, and the second is the one that motivated the work: concurrent
/// scanning must be faster, and both scans must find the *same* set of nodes. Appendix A #16 found
/// the CLI answering listings from a cache it never revalidates, so a scan that quietly returns
/// fewer nodes than it should is the actual risk here — speed that loses nodes would be worse than
/// no speedup at all.
/// </summary>
[Collection("RealCli")]
public sealed class RealCliRemoteScanThroughputTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _cliPath;
    private readonly string _scanRoot = Environment.GetEnvironmentVariable("MYPERSONALDRIVE_SCAN_ROOT") ?? "/my-files";

    public RealCliRemoteScanThroughputTests(ITestOutputHelper output)
    {
        _output = output;
        _cliPath = Environment.GetEnvironmentVariable("MYPERSONALDRIVE_CLI")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "proton-drive");
    }

    private sealed class FixedPathLocator(string path) : IProtonDriveCliLocator
    {
        public string Locate() => path;
    }

    [IntegrationFact]
    public async Task ConcurrentScanning_IsFaster_AndFindsExactlyTheSameNodes()
    {
        var mapper = new PathMapper(_scanRoot, "/tmp/mypersonaldrive-scan-probe");

        var (serialNodes, serialElapsed) = await ScanAsync(concurrency: 1, mapper);
        var (parallelNodes, parallelElapsed) = await ScanAsync(concurrency: 8, mapper);

        _output.WriteLine($"serial   : {serialNodes.Count} nodes in {serialElapsed.TotalSeconds:F1}s");
        _output.WriteLine($"parallel : {parallelNodes.Count} nodes in {parallelElapsed.TotalSeconds:F1}s");
        _output.WriteLine($"speedup  : {serialElapsed.TotalSeconds / parallelElapsed.TotalSeconds:F2}x");

        Assert.Equal(serialNodes.Keys.OrderBy(k => k), parallelNodes.Keys.OrderBy(k => k));
        Assert.True(parallelElapsed < serialElapsed,
            $"concurrent scan was not faster ({parallelElapsed} vs {serialElapsed})");
    }

    private async Task<(IReadOnlyDictionary<string, MyPersonalDrive.Models.NodeFingerprint> Nodes, TimeSpan Elapsed)> ScanAsync(
        int concurrency, PathMapper mapper)
    {
        // A fresh executor per leg, so each starts from its own empty cache root and neither leg is
        // handed the other's warm cache.
        var service = new ProtonDriveService(new ProtonDriveCliExecutor(
            new FixedPathLocator(_cliPath),
            maxReadConcurrency: concurrency,
            cacheRoot: Directory.CreateTempSubdirectory("mypersonaldrive-scan-cache").FullName));
        var scanner = new RemoteScanner(service, concurrency);

        var started = DateTimeOffset.UtcNow;
        var nodes = await scanner.ScanAsync(_scanRoot, mapper, new ExclusionMatcher());
        return (nodes, DateTimeOffset.UtcNow - started);
    }
}
