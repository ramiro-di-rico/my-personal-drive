using System.Net.Http;
using System.Security.Cryptography;
using MyPersonalDrive.Models;

using MyPersonalDrive.Services.Localization;

namespace MyPersonalDrive.Services.Providers.Proton;

/// <summary>
/// Replaces the installed `proton-drive` binary with a newer one from Proton's release manifest.
///
/// The whole design exists to make one guarantee: <b>the binary on disk is either the old working
/// one or the verified new one, never a half-written file and never an unverified one.</b> That
/// means, in order:
///
/// <list type="number">
///   <item>stream the download to a temp file <i>in the target's own directory</i>, so the final
///     step can be a rename within one filesystem rather than a copy across two;</item>
///   <item>hash while writing, and compare against the manifest's <c>Sha512CheckSum</c>;</item>
///   <item>on mismatch, delete the temp file and throw — the original is never touched;</item>
///   <item>only then set the executable bit and rename over the target.</item>
/// </list>
///
/// A rename is atomic on POSIX, and a process already running the old binary keeps its own inode,
/// so an in-flight CLI call is not corrupted by a swap underneath it.
/// </summary>
public sealed class CliUpdateInstaller
{
    private readonly Func<string, CancellationToken, Task<Stream>> _openDownload;

    /// <param name="openDownload">
    /// Opens a read stream for a URL. Injected so the verification and replacement rules can be
    /// tested without network access — the failure paths here are exactly the ones that must not
    /// be left to a manual check.
    /// </param>
    public CliUpdateInstaller(Func<string, CancellationToken, Task<Stream>>? openDownload = null)
    {
        _openDownload = openDownload ?? OpenHttpDownloadAsync;
    }

    /// <param name="targetPath">The `proton-drive` path currently in use — it gets replaced.</param>
    /// <param name="onProgress">
    /// Receives bytes-downloaded. Total size is not reported: the manifest doesn't publish a length
    /// and Content-Length can be absent under chunked encoding, so a percentage would sometimes be
    /// a lie. Callers show the byte count.
    /// </param>
    public async Task InstallAsync(
        CliReleaseCandidate release,
        string targetPath,
        Action<long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("A target path is required.", nameof(targetPath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"Cannot determine the directory of '{targetPath}'.", nameof(targetPath));
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".proton-drive-update-{Guid.NewGuid():N}");

        try
        {
            var actualHash = await DownloadToAsync(release.Url, tempPath, onProgress, cancellationToken);

            if (!HashesMatch(actualHash, release.Sha512CheckSum))
            {
                throw new CliUpdateException(
                    $"The checksum of {release.Url} does not match. Expected {Shorten(release.Sha512CheckSum)}, " +
                    $"got {Shorten(actualHash)}. The existing CLI was left untouched.",
                    LocalizedText.Of(
                        StringKeys.Error.CliUpdateChecksumMismatch,
                        release.Url,
                        Shorten(release.Sha512CheckSum),
                        Shorten(actualHash)));
            }

            if (!OperatingSystem.IsWindows())
            {
                // rwxr-xr-x, matching how the CLI is normally installed. Without this the freshly
                // written file is not executable and the swap would break every command.
                File.SetUnixFileMode(
                    tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Returns the lowercase hex SHA-512 of what was written.</summary>
    private async Task<string> DownloadToAsync(string url, string tempPath, Action<long>? onProgress, CancellationToken cancellationToken)
    {
        await using var source = await _openDownload(url, cancellationToken);
        await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        // Hash as it streams: the file is ~115 MB, so buffering it in memory to hash afterwards
        // would be a needless allocation spike, and re-reading it from disk a needless second pass.
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            onProgress?.Invoke(total);
        }

        await destination.FlushAsync(cancellationToken);
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static bool HashesMatch(string actual, string expected)
        => !string.IsNullOrWhiteSpace(expected)
            && string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string hash)
        => string.IsNullOrEmpty(hash) ? "(none)" : hash[..Math.Min(12, hash.Length)] + "…";

    private static async Task<Stream> OpenHttpDownloadAsync(string url, CancellationToken cancellationToken)
    {
        // Its own client with no overall timeout: HttpClient.Timeout covers the whole response
        // including the body, and a ~115 MB download on a slow link would trip the default 100s.
        // Cancellation is the caller's job, via the token.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new OwningStream(stream, response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is noise, not a reason to mask the real failure.
        }
    }

    /// <summary>Keeps the response and client alive for the life of the stream the caller reads.</summary>
    private sealed class OwningStream(Stream inner, HttpResponseMessage response, HttpClient client) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A refused or failed CLI update. Separate from <see cref="DriveException"/>, which means "a
/// `proton-drive` process failed" — nothing here involves running the CLI.
/// </summary>
public sealed class CliUpdateException(string message, Localization.LocalizedText detail = default)
    : InvalidOperationException(message), Localization.ILocalizedError
{
    /// <summary>See <see cref="Localization.ILocalizedError"/>.</summary>
    public Localization.LocalizedText Detail { get; } = detail;
}
