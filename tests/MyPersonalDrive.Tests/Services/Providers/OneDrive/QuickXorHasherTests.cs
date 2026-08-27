using System.Text;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers.OneDrive;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Providers.OneDrive;

/// <summary>
/// <see cref="QuickXorHasher"/>'s doc comment is explicit: this implementation is not yet verified
/// against a real Graph-reported <c>quickXorHash</c> — that happens in the live-verification
/// session (docs/PLAN-CLOUD-PROVIDERS.md Appendix A). These tests check the properties that must
/// hold regardless of whether the exact bit-level algorithm matches Microsoft's: determinism,
/// output shape, and sensitivity to input — not a specific hash literal, which would just be
/// asserting this implementation agrees with itself.
/// </summary>
public class QuickXorHasherTests : IDisposable
{
    private readonly string _tempFile = Path.GetTempFileName();

    public void Dispose() => File.Delete(_tempFile);

    /// <summary>
    /// A real ground-truth value, not a self-consistency check: this exact content was uploaded to
    /// a real OneDrive account during this phase's live-verification session, and this is the
    /// `quickXorHash` Graph reported back for it (docs/PLAN-CLOUD-PROVIDERS.md Appendix A #3). This
    /// is the test that would have caught the original wraparound bug (18 of 20 bytes matched,
    /// this pins all 20) — unlike the rest of this file's structural checks, which by design can't
    /// prove the implementation agrees with the real algorithm, only with itself.
    /// </summary>
    [Fact]
    public async Task KnownGoldenVector_MatchesGraphsRealQuickXorHash()
    {
        File.WriteAllText(_tempFile, QuickXorGoldenVector.Content);

        var hash = await new QuickXorHasher().ComputeAsync(_tempFile);

        Assert.Equal("Z2oww8eE1gMrxFl6/Q7x+ahQPvM=", hash);
    }

    [Fact]
    public void Algorithm_IsQuickXor()
    {
        Assert.Equal(RemoteHashAlgorithm.QuickXor, new QuickXorHasher().Algorithm);
    }

    [Fact]
    public async Task ComputeAsync_ProducesA20ByteHash_Base64Encoded()
    {
        File.WriteAllText(_tempFile, "hello world");
        var hash = await new QuickXorHasher().ComputeAsync(_tempFile);

        var decoded = Convert.FromBase64String(hash); // throws if not valid base64
        Assert.Equal(20, decoded.Length);
    }

    [Fact]
    public async Task ComputeAsync_IsDeterministic()
    {
        File.WriteAllText(_tempFile, "the quick brown fox jumps over the lazy dog");

        var first = await new QuickXorHasher().ComputeAsync(_tempFile);
        var second = await new QuickXorHasher().ComputeAsync(_tempFile);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ComputeAsync_DifferentContent_ProducesDifferentHashes()
    {
        File.WriteAllText(_tempFile, "content A");
        var hashA = await new QuickXorHasher().ComputeAsync(_tempFile);

        File.WriteAllText(_tempFile, "content B, a different length entirely");
        var hashB = await new QuickXorHasher().ComputeAsync(_tempFile);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public async Task ComputeAsync_EmptyFile_StillProducesA20ByteHash()
    {
        File.WriteAllText(_tempFile, string.Empty);
        var hash = await new QuickXorHasher().ComputeAsync(_tempFile);

        Assert.Equal(20, Convert.FromBase64String(hash).Length);
    }

    /// <summary>Streaming in small chunks vs. one large read must agree — the accumulator's global byte index has to survive across <see cref="QuickXorHasher.QuickXorState.Update"/> calls.</summary>
    [Fact]
    public void State_UpdateInChunks_MatchesUpdateInOneShot()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 500) + new string('y', 500));

        var oneShot = new QuickXorHasher.QuickXorState();
        oneShot.Update(content);
        var oneShotHash = Convert.ToBase64String(oneShot.Finish());

        var chunked = new QuickXorHasher.QuickXorState();
        foreach (var chunk in content.Chunk(37)) // an awkward chunk size, deliberately not aligned to any internal boundary
        {
            chunked.Update(chunk);
        }
        var chunkedHash = Convert.ToBase64String(chunked.Finish());

        Assert.Equal(oneShotHash, chunkedHash);
    }
}
