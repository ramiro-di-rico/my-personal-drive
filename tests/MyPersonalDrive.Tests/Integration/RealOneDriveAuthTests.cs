using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Tests.Services.Providers.OneDrive;
using Xunit;
using Xunit.Abstractions;

namespace MyPersonalDrive.Tests.Integration;

/// <summary>
/// Live-verification harness for P6 (docs/PLAN-CLOUD-PROVIDERS.md): a real sign-in, a real
/// <c>ListFolderAsync</c>, and a real upload compared against Graph's own <c>quickXorHash</c> for
/// that file. Everything this test confirms becomes an Appendix A finding. Cleans up its own test
/// file (trash, never permanent delete — same rule the Proton integration tests follow).
///
/// The authorize URL is written to a file immediately when it's known (not just to
/// <see cref="ITestOutputHelper"/>, which most test runners buffer until the test finishes — no
/// good for a URL that has to be opened *while* the test is still blocked waiting on the redirect).
/// </summary>
public sealed class RealOneDriveAuthTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("mypersonaldrive-onedrive-integration").FullName;
    private readonly string _authUrlFile;

    public RealOneDriveAuthTests(ITestOutputHelper output)
    {
        _output = output;
        _authUrlFile = Environment.GetEnvironmentVariable("MYPERSONALDRIVE_ONEDRIVE_AUTH_URL_FILE")
            ?? Path.Combine(_tempDir, "authorize-url.txt");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [OneDriveIntegrationFact]
    public async Task SignIn_ListRoot_UploadAndCompareQuickXorHash()
    {
        var clientId = Environment.GetEnvironmentVariable(OneDriveIntegrationFactAttribute.ClientIdEnvironmentVariable)!;
        var tokenStore = new OneDriveTokenStore(_tempDir);
        var authenticator = new GraphAuthenticator(clientId, tokenStore);
        authenticator.Activity += (_, activity) =>
        {
            if (activity.Kind == ActivityKind.Started && activity.Text is { } url)
            {
                File.WriteAllText(_authUrlFile, url);
                _output.WriteLine($"AUTHORIZE URL written to {_authUrlFile}: {url}");
            }
        };

        await authenticator.AuthenticateAsync();
        _output.WriteLine($"Signed in. Account label: {authenticator.AccountLabel ?? "(none returned)"}");

        var http = new GraphHttpClient(authenticator);
        var operations = new OneDriveOperations(http);

        // Finding #1: a real listing of the account's root.
        var root = await operations.ListFolderAsync("/");
        _output.WriteLine($"Root has {root.Count} children: {string.Join(", ", root.Take(10).Select(i => i.Name))}");
        Assert.NotNull(root);

        // Finding #2: QuickXorHasher's local computation vs. what Graph reports for the same file.
        var fileName = $"mypersonaldrive-p6-verification-{Guid.NewGuid():N}.txt";
        var localPath = Path.Combine(_tempDir, fileName);
        // Fixed, not timestamp-based: deliberately the same content
        // QuickXorHasherTests.KnownGoldenVector pins as a literal, captured from this exact test
        // against a real account (docs/PLAN-CLOUD-PROVIDERS.md Appendix A #3) — 81 bytes, long
        // enough to exercise every wraparound-boundary byte index (14/29/43/58 mod 160 at
        // Shift=11) the original bug silently dropped.
        await File.WriteAllTextAsync(localPath, QuickXorGoldenVector.Content);

        try
        {
            await operations.UploadFilesAsync([localPath], "/", UploadConflictStrategy.Replace);

            var listing = await operations.ListFolderAsync("/");
            var uploaded = listing.SingleOrDefault(i => i.Name == fileName);
            Assert.NotNull(uploaded);

            var localHash = await new QuickXorHasher().ComputeAsync(localPath);
            _output.WriteLine($"local quickXorHash:  {localHash}");
            _output.WriteLine($"Graph quickXorHash:  {uploaded.ContentHash ?? "(none returned)"}");

            Assert.NotNull(uploaded.ContentHash);
            Assert.Equal(uploaded.ContentHash, localHash);
        }
        finally
        {
            // Best-effort cleanup: leave the account as we found it regardless of whether the
            // assertions above passed.
            try
            {
                await operations.TrashItemAsync($"/{fileName}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup trash failed (not fatal to the test result): {ex.Message}");
            }
        }
    }
}
