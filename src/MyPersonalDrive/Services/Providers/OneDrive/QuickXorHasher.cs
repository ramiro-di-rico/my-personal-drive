using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Computes OneDrive's QuickXorHash, so a local file's hash is directly comparable to the
/// <c>file.hashes.quickXorHash</c> Graph reports (docs/PLAN-CLOUD-PROVIDERS.md §4.4/O4).
///
/// <b>Implemented from Microsoft's published algorithm description, NOT yet verified against a
/// real Graph-reported hash.</b> Per this session's plan, that verification happens by uploading a
/// small file during the live sign-in session and comparing this class's output against the
/// `quickXorHash` Graph reports for that same file — the result becomes Appendix A finding #2 in
/// docs/PLAN-CLOUD-PROVIDERS.md. Until that runs, treat this as unconfirmed: a wrong-but-consistent
/// hash here fails safe (files compare as permanently "changed", forcing extra transfers) rather
/// than unsafe (P3's `IsAlgorithmMismatch`/`IsKnownAlgorithm` guard only protects against comparing
/// hashes from two different algorithms, not against one algorithm's implementation being wrong).
/// </summary>
public sealed class QuickXorHasher : IContentHasher
{
    public RemoteHashAlgorithm Algorithm => RemoteHashAlgorithm.QuickXor;

    public async Task<string> ComputeAsync(string localPath, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var state = new QuickXorState();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            state.Update(buffer.AsSpan(0, read));
        }

        return Convert.ToBase64String(state.Finish());
    }

    /// <summary>
    /// The algorithm itself, isolated so it can be driven directly by unit tests over in-memory
    /// byte arrays without a temp file.
    /// </summary>
    internal struct QuickXorState
    {
        private const int WidthInBits = 160;
        private const int Shift = 11;
        private const int DataLength = (WidthInBits - 1) / 64 + 1; // 3 ulongs = 192 bits of storage for a 160-bit accumulator

        private readonly ulong[] _data;
        private long _lengthSoFar;

        public QuickXorState()
        {
            _data = new ulong[DataLength];
            _lengthSoFar = 0;
        }

        /// <summary>
        /// XORs each byte into the 160-bit accumulator at bit position <c>(Shift * globalIndex) mod
        /// WidthInBits</c>, where <c>globalIndex</c> is the byte's 0-based offset in the whole file
        /// (tracked across calls via <see cref="_lengthSoFar"/>, not reset per chunk).
        /// </summary>
        public void Update(ReadOnlySpan<byte> chunk)
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                var globalIndex = _lengthSoFar + i;
                var bitPosition = (int)(Shift * (globalIndex % WidthInBits)) % WidthInBits;
                var elementIndex = bitPosition / 64;
                var bitOffset = bitPosition % 64;

                var value = (ulong)chunk[i];
                _data[elementIndex] ^= value << bitOffset;
                // A shift landing in the last 7 bits of a ulong pushes part of the byte's 8 bits
                // into the next element — carry that spillover across the boundary.
                if (bitOffset > 64 - 8 && elementIndex + 1 < DataLength)
                {
                    _data[elementIndex + 1] ^= value >> (64 - bitOffset);
                }
            }

            _lengthSoFar += chunk.Length;
        }

        public readonly byte[] Finish()
        {
            var result = new byte[WidthInBits / 8];
            for (var i = 0; i < DataLength; i++)
            {
                var bytes = BitConverter.GetBytes(_data[i]);
                var copyLength = Math.Min(8, result.Length - i * 8);
                if (copyLength <= 0)
                {
                    break;
                }

                Array.Copy(bytes, 0, result, i * 8, copyLength);
            }

            // The total byte count, little-endian, XORed into the last 8 bytes of the digest.
            var lengthBytes = BitConverter.GetBytes(_lengthSoFar);
            for (var i = 0; i < 8; i++)
            {
                result[^(8 - i)] ^= lengthBytes[i];
            }

            return result;
        }
    }
}
