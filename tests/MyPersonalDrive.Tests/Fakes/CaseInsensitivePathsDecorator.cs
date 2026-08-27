using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;

namespace MyPersonalDrive.Tests.Fakes;

/// <summary>
/// Wraps a real provider, overriding only <see cref="IProviderPathSyntax.Comparison"/> to be
/// case-insensitive. Proton itself is always case-sensitive, so this is how tests exercise the
/// case-insensitive-provider code paths (docs/PLAN-CLOUD-PROVIDERS.md §2.4) without a real
/// case-insensitive provider existing yet.
/// </summary>
public sealed class CaseInsensitivePathsDecorator : ICloudDriveProvider
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

    private sealed class FakePathSyntax : IProviderPathSyntax
    {
        public FakePathSyntax(StringComparison comparison) => Comparison = comparison;
        public StringComparison Comparison { get; }
        public string Combine(string parentPath, string name) => ProtonDriveService.CombinePath(parentPath, name);
        public bool IsRemoteNameMappableLocally(string name) => !ProtonDriveService.HasUnmappableName(name);
    }
}
