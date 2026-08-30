using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Providers.OneDrive;

/// <summary>
/// Computes OneDrive's QuickXorHash, so a local file's hash is directly comparable to the
/// <c>file.hashes.quickXorHash</c> Graph reports (docs/PLAN-CLOUD-PROVIDERS.md §4.4/O4).
///
/// Verified against a real Graph-reported <c>quickXorHash</c> during this phase's
/// live-verification session (docs/PLAN-CLOUD-PROVIDERS.md Appendix A #2): the first attempt
/// (a packed <c>ulong[3]</c> accumulator) matched on 18 of 20 bytes but was wrong at the circular
/// wraparound boundary — see <see cref="QuickXorState"/>'s own doc comment for the specific bug.
/// Confirmed matching Graph's own value on two separate uploads after the fix.
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
    ///
    /// The accumulator is a genuinely circular 160-bit (20-byte) buffer — <b>not</b> a 192-bit
    /// (3×ulong) one. An earlier version of this code stored the accumulator as
    /// <c>ulong[3]</c> and only handled overflow across a 64-bit array-element boundary; that
    /// missed the case where a byte's 8-bit span crosses the *logical* 160-bit wraparound point
    /// while still sitting entirely inside one 64-bit element (e.g. shift position 154: bits
    /// 154–161 fit inside the third ulong with room to spare, but bits 160–161 are past the
    /// 160-bit width and must fold back to bit 0, not sit unused past byte 19 where
    /// <c>Finish()</c> would silently discard them). Found and fixed against a real
    /// Graph-reported <c>quickXorHash</c> during this phase's live-verification session
    /// (docs/PLAN-CLOUD-PROVIDERS.md Appendix A) — the byte-array-of-bits model here is
    /// deliberately simple (a per-bit XOR loop, not a packed-word shift trick) specifically so a
    /// second subtle boundary case doesn't have anywhere to hide.
    /// </summary>
    internal struct QuickXorState
    {
        private const int OutputBits = 160; // 20-byte digest
        private const int Shift = 11;

        private readonly byte[] _data;
        private long _lengthSoFar;

        public QuickXorState()
        {
            _data = new byte[OutputBits / 8];
            _lengthSoFar = 0;
        }

        /// <summary>
        /// XORs each byte into the circular 160-bit accumulator at bit position <c>(Shift *
        /// globalIndex) mod OutputBits</c>, where <c>globalIndex</c> is the byte's 0-based offset
        /// in the whole file (tracked across calls via <see cref="_lengthSoFar"/>, not reset per
        /// chunk).
        /// </summary>
        public void Update(ReadOnlySpan<byte> chunk)
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                var globalIndex = _lengthSoFar + i;
                var bitPosition = (int)(Shift * (globalIndex % OutputBits)) % OutputBits;
                XorByteAtBitPosition(_data, bitPosition, chunk[i]);
            }

            _lengthSoFar += chunk.Length;
        }

        /// <summary>
        /// XORs each of <paramref name="value"/>'s 8 bits (bit 0 = least significant) into
        /// <paramref name="data"/>'s bit array, starting at <paramref name="bitPosition"/> and
        /// wrapping circularly modulo the array's total bit width. One bit at a time rather than a
        /// packed multi-byte shift: correctness here matters more than the constant-factor speed a
        /// wider write would buy, and per-bit is what makes the circular wrap trivially correct at
        /// every position, aligned or not.
        /// </summary>
        private static void XorByteAtBitPosition(byte[] data, int bitPosition, byte value)
        {
            var widthBits = data.Length * 8;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((value & (1 << bit)) == 0)
                {
                    continue;
                }

                var targetBit = (bitPosition + bit) % widthBits;
                data[targetBit / 8] ^= (byte)(1 << (targetBit % 8));
            }
        }

        public readonly byte[] Finish()
        {
            var result = (byte[])_data.Clone();

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
