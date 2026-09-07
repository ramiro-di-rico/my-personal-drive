using System.Net.Http;
using System.Text.Json;
using Avalonia.Threading;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Localization;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;

namespace MyPersonalDrive.ViewModels;

/// <summary>
/// The Proton CLI's installed version and its self-update: check the version, ask the release feed
/// what the latest Stable is, and swap the binary (docs/ARCHITECTURE.md §10).
///
/// Z5 step 1 (docs/PLAN-UX-ROUND-4.md#z5). The first cluster out of the 4400-line view model, and
/// the smallest: everything here needs the release feed, the installer and somewhere to report, and
/// nothing else in that class needs any of them. It could only be extracted once
/// <see cref="StatusSurface"/> existed, because reporting was the coupling.
///
/// Its dependencies arrive as accessors rather than values because they move underneath it: the
/// active provider changes when the user switches account, and the CLI path is a settings field the
/// user edits while this view model is alive.
/// </summary>
public sealed class CliUpdateViewModel : ObservableObject
{
    private readonly Func<ICloudDriveProvider> _provider;
    private readonly ICliReleaseFeed? _releaseFeed;
    private readonly CliUpdateInstaller _installer;
    private readonly Func<string> _cliPath;
    private readonly Func<bool> _isSyncInProgress;
    private readonly StatusSurface _status;

    private CliReleaseCandidate? _availableRelease;
    private string _cliVersion = UnknownCliVersion;
    private string _cliUpdateStatus = Localizer.Instance.T(StringKeys.CliUpdate.Unchecked);
    private bool _isCliUpdateAvailable;
    private bool _isCliUpdateBusy;
    private bool _isCheckingCliVersion;

    /// <summary>
    /// The unrendered forms behind <see cref="CliVersion"/> and <see cref="CliUpdateStatus"/>. Both
    /// carry the result of a past operation, and storing them as rendered prose froze them in
    /// whichever language was current when the check ran (docs/PLAN-UX-ROUND-4.md Y7).
    /// </summary>
    private LocalizedText _cliVersionText = LocalizedText.Of(StringKeys.Common.Unknown);

    private LocalizedText _cliUpdateStatusText = LocalizedText.Of(StringKeys.CliUpdate.Unchecked);

    internal CliUpdateViewModel(
        Func<ICloudDriveProvider> provider,
        ICliReleaseFeed? releaseFeed,
        CliUpdateInstaller installer,
        Func<string> cliPath,
        Func<bool> isSyncInProgress,
        StatusSurface status,
        Action<Exception> onError)
    {
        _provider = provider;
        _releaseFeed = releaseFeed;
        _installer = installer;
        _cliPath = cliPath;
        _isSyncInProgress = isSyncInProgress;
        _status = status;

        CheckCliVersionCommand = new AsyncCommand(CheckCliVersionAsync, CanCheckCliVersion, onError);
        CheckForCliUpdateCommand = new AsyncCommand(CheckForCliUpdateAsync, CanCheckForCliUpdate, onError);
        InstallCliUpdateCommand = new AsyncCommand(InstallCliUpdateAsync, CanInstallCliUpdate, onError);
    }

    private static string UnknownCliVersion => Localizer.Instance.T(StringKeys.Common.Unknown);

    /// <summary>Whether the active provider has an external binary to version at all.</summary>
    public bool HasDiagnostics => _provider().Diagnostics is not null;

    public string CliVersion
    {
        get => _cliVersion;
        private set => SetProperty(ref _cliVersion, value);
    }

    /// <summary>Human-readable result of the last update check, or the progress of a running install.</summary>
    public string CliUpdateStatus
    {
        get => _cliUpdateStatus;
        private set => SetProperty(ref _cliUpdateStatus, value);
    }

    /// <summary>
    /// True only when a newer Stable release was positively identified for this platform. An
    /// unreadable installed version leaves this false — see <see cref="CliUpdateAvailability"/>.
    /// </summary>
    public bool IsCliUpdateAvailable
    {
        get => _isCliUpdateAvailable;
        private set
        {
            if (SetProperty(ref _isCliUpdateAvailable, value))
            {
                InstallCliUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCliUpdateBusy
    {
        get => _isCliUpdateBusy;
        private set
        {
            if (SetProperty(ref _isCliUpdateBusy, value))
            {
                CheckForCliUpdateCommand.RaiseCanExecuteChanged();
                InstallCliUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCheckingCliVersion
    {
        get => _isCheckingCliVersion;
        private set
        {
            if (SetProperty(ref _isCheckingCliVersion, value))
            {
                CheckCliVersionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand CheckCliVersionCommand { get; }

    public AsyncCommand CheckForCliUpdateCommand { get; }

    public AsyncCommand InstallCliUpdateCommand { get; }

    /// <summary>
    /// Reads the version once per configured path. The settings view calls this on the way in, so
    /// it never shows a stale or empty version — and only once, because the CLI costs a whole
    /// process launch (~3.5s cold).
    /// </summary>
    public async Task EnsureVersionReadAsync()
    {
        if (CliVersionIsUnknown && !string.IsNullOrWhiteSpace(_cliPath()))
        {
            await CheckCliVersionAsync();
        }
    }

    /// <summary>
    /// A different executable is a different version: what was read no longer applies, and neither
    /// does an update offer computed against the old one. Called by the view model when the CLI
    /// path changes.
    /// </summary>
    public void Reset()
    {
        SetCliVersion(LocalizedText.Of(StringKeys.Common.Unknown));
        _availableRelease = null;
        IsCliUpdateAvailable = false;
        SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Unchecked));
        RaiseCommandStates();
    }

    /// <summary>Re-renders both stored results after a language change (docs/PLAN-UX-ROUND-4.md Y7).</summary>
    public void OnLanguageChanged()
    {
        CliVersion = _cliVersionText.Render();
        CliUpdateStatus = _cliUpdateStatusText.Render();
        OnAllPropertiesChanged();
    }

    /// <summary>Announces the command states that depend on state living outside this view model.</summary>
    public void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(HasDiagnostics));
        CheckCliVersionCommand.RaiseCanExecuteChanged();
        CheckForCliUpdateCommand.RaiseCanExecuteChanged();
        InstallCliUpdateCommand.RaiseCanExecuteChanged();
    }

    private void SetCliVersion(LocalizedText text)
    {
        _cliVersionText = text;
        CliVersion = text.Render();
    }

    private void SetCliUpdateStatus(LocalizedText text)
    {
        _cliUpdateStatusText = text;
        CliUpdateStatus = text.Render();
    }

    /// <summary>Whether the installed version is still the "not checked yet" placeholder. Compared on the key, not on the rendered prose — that comparison silently stopped matching after a language change.</summary>
    private bool CliVersionIsUnknown => _cliVersionText.Key == StringKeys.Common.Unknown;

    private bool CanCheckCliVersion() => !_isCheckingCliVersion && !string.IsNullOrWhiteSpace(_cliPath());

    private bool CanCheckForCliUpdate() => _releaseFeed is not null && !IsCliUpdateBusy;

    private bool CanInstallCliUpdate()
        => IsCliUpdateAvailable && _availableRelease is not null && !IsCliUpdateBusy && !string.IsNullOrWhiteSpace(_cliPath());

    private async Task CheckCliVersionAsync()
    {
        IsCheckingCliVersion = true;
        try
        {
            // Diagnostics is only ever null for a provider with no external binary to version
            // (docs/PLAN-CLOUD-PROVIDERS.md §2.6); the settings UI stops offering this command for
            // such a provider as of P5. Today's only provider (Proton) always has one.
            var diagnostics = _provider().Diagnostics;
            var version = diagnostics is not null ? await diagnostics.GetVersionAsync() : null;
            SetCliVersion(string.IsNullOrWhiteSpace(version)
                ? LocalizedText.Of(StringKeys.CliVersion.NoVersionReported)
                : LocalizedText.Verbatim(version));
        }
        catch (InvalidOperationException ex)
        {
            // Includes DriveException. The CLI's own text is the most useful thing on screen here:
            // if `--version` is not the flag this build understands, the user sees exactly that.
            SetCliVersion(LocalizedText.Of(StringKeys.CliVersion.Unavailable, ex.DescribeForUser().Render()));
        }
        catch (FileNotFoundException ex)
        {
            SetCliVersion(LocalizedText.Of(StringKeys.CliVersion.Unavailable, ex.DescribeForUser().Render()));
        }
        finally
        {
            IsCheckingCliVersion = false;
        }
    }

    private async Task CheckForCliUpdateAsync()
    {
        if (_releaseFeed is null)
        {
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Unavailable));
            return;
        }

        IsCliUpdateBusy = true;
        try
        {
            // The comparison needs a version to compare against, and the user may never have
            // opened the settings view this session.
            if (CliVersionIsUnknown && !string.IsNullOrWhiteSpace(_cliPath()))
            {
                await CheckCliVersionAsync();
            }

            var release = await _releaseFeed.GetLatestStableAsync();
            if (release is null)
            {
                _availableRelease = null;
                IsCliUpdateAvailable = false;
                SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.NoBuildForPlatform));
                return;
            }

            switch (CliVersionComparer.Compare(CliVersion, release.Version))
            {
                case CliUpdateAvailability.UpdateAvailable:
                    _availableRelease = release;
                    IsCliUpdateAvailable = true;
                    SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Available, release.Version, release.ReleaseDate));
                    break;

                case CliUpdateAvailability.UpToDate:
                    _availableRelease = null;
                    IsCliUpdateAvailable = false;
                    SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.UpToDate, release.Version));
                    break;

                default:
                    // Refusing to offer an install here is the point: overwriting a working CLI on
                    // the strength of a version string we couldn't read is the one outcome worse
                    // than not updating.
                    _availableRelease = null;
                    IsCliUpdateAvailable = false;
                    SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.InstalledVersionUnknown, release.Version));
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _availableRelease = null;
            IsCliUpdateAvailable = false;
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.ManifestUnreachable, ex.Message));
        }
        finally
        {
            IsCliUpdateBusy = false;
        }
    }

    private async Task InstallCliUpdateAsync()
    {
        var release = _availableRelease;
        if (release is null)
        {
            return;
        }

        // A scan or transfer in flight is holding the CLI. The rename itself is atomic and an
        // already-running process keeps its own inode, so this is not about corrupting the swap —
        // it is that the next call in that same cycle would land on a different binary version
        // mid-operation, which is not a state worth reasoning about.
        if (_isSyncInProgress())
        {
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.SyncInProgress));
            return;
        }

        IsCliUpdateBusy = true;
        try
        {
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Downloading, release.Version));
            await _installer.InstallAsync(
                release,
                _cliPath(),
                onProgress: bytes => Dispatcher.UIThread.Post(
                    () => SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.DownloadingWithSize, release.Version, bytes / (1024 * 1024)))));

            _availableRelease = null;
            IsCliUpdateAvailable = false;
            SetCliVersion(LocalizedText.Of(StringKeys.Common.Unknown));
            await CheckCliVersionAsync();
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Done, release.Version));
        }
        catch (CliUpdateException ex)
        {
            // Includes the checksum mismatch, which leaves the old binary in place by design.
            SetCliUpdateStatus(LocalizedText.Verbatim(ex.Message));
            _status.Warn();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            SetCliUpdateStatus(LocalizedText.Of(StringKeys.CliUpdate.Failed, ex.DescribeForUser().Render()));
            _status.Warn();
        }
        finally
        {
            IsCliUpdateBusy = false;
        }
    }
}
