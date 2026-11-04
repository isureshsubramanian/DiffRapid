using System;
using System.IO;
using System.Threading;

namespace DiffRapid.Core.Folders;

/// <summary>
/// The 64-bit xxHash of a byte sequence (seed 0), computed in a single streaming pass. Used to match files by
/// content; it is fast but not cryptographic.
/// </summary>
public static class XxHash64
{
    private const ulong Prime1 = 0x9E3779B185EBCA87UL;
    private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Prime3 = 0x165667B19E3779F9UL;
    private const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
    private const ulong Prime5 = 0x27D4EB2F165667C5UL;
    private const int StripeSize = 32;
    private const int BufferSize = 64 * 1024;

    /// <summary>Hashes a byte array.</summary>
    public static ulong Compute(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        return Compute(data, 0, data.Length);
    }

    /// <summary>Hashes a range of a byte array.</summary>
    public static ulong Compute(byte[] data, int offset, int count)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (offset < 0 || count < 0 || offset > data.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var state = new State();
        var stripes = count / StripeSize * StripeSize;
        state.Stripes(data, offset, stripes);
        return state.Finish(data, offset + stripes, count - stripes);
    }

    /// <summary>Hashes a stream from its current position to the end.</summary>
    public static ulong Compute(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var state = new State();
        var buffer = new byte[BufferSize];
        var filled = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                return state.Finish(buffer, 0, filled);
            }

            filled += read;
            var stripes = filled / StripeSize * StripeSize;
            state.Stripes(buffer, 0, stripes);
            filled -= stripes;
            Buffer.BlockCopy(buffer, stripes, buffer, 0, filled);
        }
    }

    /// <summary>Hashes a file's contents.</summary>
    public static ulong ComputeFile(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        return Compute(stream, cancellationToken);
    }

    private static ulong Round(ulong accumulator, ulong input)
    {
        accumulator += input * Prime2;
        accumulator = RotateLeft(accumulator, 31);
        return accumulator * Prime1;
    }

    private static ulong MergeRound(ulong accumulator, ulong value)
    {
        accumulator ^= Round(0, value);
        return (accumulator * Prime1) + Prime4;
    }

    private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

    private static ulong Read64(byte[] data, int index) =>
        Read32(data, index) | ((ulong)Read32(data, index + 4) << 32);

    private static uint Read32(byte[] data, int index) =>
        data[index] | ((uint)data[index + 1] << 8) | ((uint)data[index + 2] << 16) | ((uint)data[index + 3] << 24);

    private sealed class State
    {
        private ulong _v1 = unchecked(Prime1 + Prime2);
        private ulong _v2 = Prime2;
        private ulong _v3;
        private ulong _v4 = unchecked(0 - Prime1);
        private ulong _length;

        /// <summary>Consumes whole 32-byte stripes; <paramref name="count"/> is a multiple of 32.</summary>
        public void Stripes(byte[] data, int offset, int count)
        {
            for (var i = offset; i < offset + count; i += StripeSize)
            {
                _v1 = Round(_v1, Read64(data, i));
                _v2 = Round(_v2, Read64(data, i + 8));
                _v3 = Round(_v3, Read64(data, i + 16));
                _v4 = Round(_v4, Read64(data, i + 24));
            }

            _length += (ulong)count;
        }

        /// <summary>Consumes the final partial stripe (under 32 bytes) and returns the hash.</summary>
        public ulong Finish(byte[] data, int offset, int count)
        {
            var hash = _length >= StripeSize
                ? MergedStripes()
                : Prime5;
            hash += _length + (ulong)count;

            var index = offset;
            var end = offset + count;
            while (index + 8 <= end)
            {
                hash ^= Round(0, Read64(data, index));
                hash = (RotateLeft(hash, 27) * Prime1) + Prime4;
                index += 8;
            }

            if (index + 4 <= end)
            {
                hash ^= Read32(data, index) * Prime1;
                hash = (RotateLeft(hash, 23) * Prime2) + Prime3;
                index += 4;
            }

            while (index < end)
            {
                hash ^= data[index] * Prime5;
                hash = RotateLeft(hash, 11) * Prime1;
                index++;
            }

            return Avalanche(hash);
        }

        private ulong MergedStripes()
        {
            var hash = RotateLeft(_v1, 1) + RotateLeft(_v2, 7) + RotateLeft(_v3, 12) + RotateLeft(_v4, 18);
            hash = MergeRound(hash, _v1);
            hash = MergeRound(hash, _v2);
            hash = MergeRound(hash, _v3);
            return MergeRound(hash, _v4);
        }

        private static ulong Avalanche(ulong hash)
        {
            hash ^= hash >> 33;
            hash *= Prime2;
            hash ^= hash >> 29;
            hash *= Prime3;
            return hash ^ (hash >> 32);
        }
    }
}
