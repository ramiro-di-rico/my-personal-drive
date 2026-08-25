namespace MyPersonalDrive.Models;

/// <summary>
/// Which algorithm produced a <see cref="NodeFingerprint.ContentHash"/> or a provider's
/// <c>Capabilities.RemoteHash</c>. Lives in <c>Models</c> rather than <c>Services.Providers</c>
/// because <see cref="NodeFingerprint"/> — a plain data record — needs it too; see
/// docs/PLAN-CLOUD-PROVIDERS.md §2.5/§3 (P3).
/// </summary>
public enum RemoteHashAlgorithm
{
    None,
    Sha1,
    Sha256,
    QuickXor
}
