using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The update check and install at the view-model level. <see cref="Services.CliUpdateInstallerTests"/>
/// covers the on-disk guarantees; what's proven here is the decision to offer an install at all —
/// and the refusals, which are the part that protects a working CLI.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowCliUpdateTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.CliUpdate").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-cli-update-vm-{Guid.NewGuid():N}.db");

    public MainWindowCliUpdateTests()
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
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static readonly CliReleaseCandidate Stable070 = new(
        "0.7.0",
        "2026-07-31",
        "https://proton.me/download/drive/cli/0.7.0/linux-x64/proton-drive",
        "5a5affcbec04ea926a32d10e236c1342227f1b6d416cb797f88f943b2c4f1dcf53b5897a115f1c1aa9ce8ce92fd637e1c50bd223b04866577681f0584eccdbc6",
        "linux/x64");

    /// <summary>Real captured output of the installed 0.6.0 binary.</summary>
    private const string InstalledVersionOutput = "Proton Drive CLI cli-drive@0.6.0+f8e16aac\nProton Drive SDK js@0.19.2+f8e16aac\n";

    private (MainWindowViewModel ViewModel, FakeCliExecutor Executor, SyncPanelViewModel Panel) Build(
        ICliReleaseFeed? feed = null,
        CliUpdateInstaller? installer = null,
        string cliPath = "/usr/bin/proton-drive",
        Func<bool>? isSyncInProgress = null)
    {
        var executor = new FakeCliExecutor();
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var viewModel = new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            releaseFeed: feed,
            updateInstaller: installer,
            isSyncInProgress: isSyncInProgress)
        {
            CliPath = cliPath
        };

        return (viewModel, executor, panel);
    }

    [Fact]
    public async Task Installed060_AgainstStable070_OffersTheUpdate()
    {
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(Stable070));
        executor.EnqueueOutput(InstalledVersionOutput);

        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();

        Assert.True(viewModel.IsCliUpdateAvailable);
        Assert.Contains("0.7.0", viewModel.CliUpdateStatus);
        Assert.Contains("2026-07-31", viewModel.CliUpdateStatus);
    }

    [Fact]
    public async Task WhenAlreadyOnStable_NoUpdateIsOffered()
    {
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(Stable070));
        executor.EnqueueOutput("Proton Drive CLI cli-drive@0.7.0+f8e16aac\n");

        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();

        Assert.False(viewModel.IsCliUpdateAvailable);
        Assert.Contains("Up to date", viewModel.CliUpdateStatus);
    }

    /// <summary>
    /// The refusal that matters most: if the installed version can't be read, the app must not
    /// offer to overwrite a working binary on the strength of a guess.
    /// </summary>
    [Fact]
    public async Task WhenTheInstalledVersionCannotBeRead_NoUpdateIsOffered()
    {
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(Stable070));
        executor.EnqueueFailure(new CliException("--version", 1, string.Empty, "unknown flag", "unknown flag: --version"));

        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();

        Assert.False(viewModel.IsCliUpdateAvailable);
        Assert.False(viewModel.InstallCliUpdateCommand.CanExecute(null));
        Assert.Contains("could not be read", viewModel.CliUpdateStatus);
    }

    [Fact]
    public async Task WhenTheManifestIsUnreachable_ItIsReportedNotThrown()
    {
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(failure: new HttpRequestException("no route to host")));
        executor.EnqueueOutput(InstalledVersionOutput);

        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();

        Assert.False(viewModel.IsCliUpdateAvailable);
        Assert.Contains("Could not reach", viewModel.CliUpdateStatus);
    }

    [Fact]
    public async Task WhenNoBuildExistsForThisPlatform_NoUpdateIsOffered()
    {
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(candidate: null));
        executor.EnqueueOutput(InstalledVersionOutput);

        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();

        Assert.False(viewModel.IsCliUpdateAvailable);
        Assert.Contains("no Stable build", viewModel.CliUpdateStatus);
    }

    [Fact]
    public async Task WithNoReleaseFeedWired_TheCheckIsUnavailableRatherThanCrashing()
    {
        var (viewModel, _, _) = Build(feed: null);

        Assert.False(viewModel.CheckForCliUpdateCommand.CanExecute(null));
        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();
        Assert.False(viewModel.IsCliUpdateAvailable);
    }

    [Fact]
    public async Task InstallingAVerifiedUpdate_ReplacesTheBinaryAndRereadsTheVersion()
    {
        var target = Path.Combine(_tempAppData, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");
        var payload = "new binary";
        var release = new CliReleaseCandidate(
            "0.7.0", "2026-07-31", "https://proton.me/x",
            Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(payload))), "linux/x64");

        var installer = new CliUpdateInstaller((_, _) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload))));
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(release), installer, cliPath: target);

        executor.EnqueueOutput(InstalledVersionOutput);
        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();
        Assert.True(viewModel.IsCliUpdateAvailable);

        // The version re-read after the swap.
        executor.EnqueueOutput("Proton Drive CLI cli-drive@0.7.0+aabbccdd\n");
        await viewModel.InstallCliUpdateCommand.ExecuteAsync();

        Assert.Equal(payload, await File.ReadAllTextAsync(target));
        Assert.Equal("Proton Drive CLI cli-drive@0.7.0+aabbccdd", viewModel.CliVersion);
        Assert.False(viewModel.IsCliUpdateAvailable);
        Assert.Contains("Updated to 0.7.0", viewModel.CliUpdateStatus);
    }

    [Fact]
    public async Task AChecksumMismatch_IsSurfaced_AndTheOldBinaryKept()
    {
        var target = Path.Combine(_tempAppData, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");
        var release = new CliReleaseCandidate("0.7.0", "2026-07-31", "https://proton.me/x", Convert.ToHexStringLower(SHA512.HashData("promised"u8.ToArray())), "linux/x64");

        var installer = new CliUpdateInstaller((_, _) => Task.FromResult<Stream>(new MemoryStream("tampered"u8.ToArray())));
        var (viewModel, executor, _) = Build(new FakeCliReleaseFeed(release), installer, cliPath: target);

        executor.EnqueueOutput(InstalledVersionOutput);
        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();
        await viewModel.InstallCliUpdateCommand.ExecuteAsync();

        Assert.Contains("Checksum mismatch", viewModel.CliUpdateStatus);
        Assert.Equal("old binary", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// Swapping the executable out from under a running sync cycle is refused. The download must not
    /// even start.
    /// </summary>
    [Fact]
    public async Task WhileASyncIsRunning_TheInstallIsRefused()
    {
        var target = Path.Combine(_tempAppData, "proton-drive");
        await File.WriteAllTextAsync(target, "old binary");
        var installer = new CliUpdateInstaller((_, _) => throw new InvalidOperationException("the download must not start"));
        var syncing = false;
        var (viewModel, executor, _) = Build(
            new FakeCliReleaseFeed(Stable070), installer, cliPath: target, isSyncInProgress: () => syncing);

        executor.EnqueueOutput(InstalledVersionOutput);
        await viewModel.CheckForCliUpdateCommand.ExecuteAsync();
        Assert.True(viewModel.IsCliUpdateAvailable);

        syncing = true;
        await viewModel.InstallCliUpdateCommand.ExecuteAsync();

        Assert.Contains("A sync is running", viewModel.CliUpdateStatus);
        Assert.Equal("old binary", await File.ReadAllTextAsync(target));
    }
}
