using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Algorithms;

namespace DiffRapid.Core.Binary;

/// <summary>
/// Finds insertions and deletions between two byte arrays. Both arrays are cut into content-defined chunks
/// with a rolling hash, so an insertion only changes the chunks around it; the chunk sequences are diffed
/// with the histogram algorithm, and each changed region is then narrowed to the exact bytes.
/// </summary>
internal static class ResyncDiff
{
    /// <summary>Changed regions at most this long (both sides together) are diffed byte by byte.</summary>
    internal const int ByteDiffLimit = 1024;

    /// <summary>Byte-level matches shorter than this inside a changed region are folded into the change.</summary>
    internal const int MinimumEqualRun = 4;

    private const int MinimumChunk = 32;
    private const int MaximumChunk = 512;
    // The top bits of the gear hash depend on the last 64 bytes; six of them give chunks of about 64 bytes past the minimum.
    private const ulong BoundaryMask = 0xFC00000000000000UL;

    private static readonly ulong[] Gear = CreateGearTable();

    public static List<BinaryDiffRange> Compare(byte[] left, byte[] right, CancellationToken cancellationToken)
    {
        var ranges = new List<BinaryDiffRange>();
        var prefix = CommonPrefix(left, right);
        var suffix = CommonSuffix(left, right, prefix);
        var leftEnd = left.Length - suffix;
        var rightEnd = right.Length - suffix;
        if (prefix == leftEnd && prefix == rightEnd)
        {
            return ranges;
        }

        if (prefix == leftEnd || prefix == rightEnd)
        {
            ranges.Add(BinaryDiffRange.Create(prefix, leftEnd - prefix, prefix, rightEnd - prefix));
            return ranges;
        }

        var leftChunks = Chunk(left, prefix, leftEnd, cancellationToken);
        var rightChunks = Chunk(right, prefix, rightEnd, cancellationToken);
        var leftTokens = new int[leftChunks.Count - 1];
        var rightTokens = new int[rightChunks.Count - 1];
        Intern(left, leftChunks, right, rightChunks, leftTokens, rightTokens);

        var leftChanged = new bool[leftTokens.Length];
        var rightChanged = new bool[rightTokens.Length];
        new HistogramDiff().Compare(leftTokens, rightTokens, leftChanged, rightChanged, cancellationToken);
        foreach (var region in ChangedRuns(leftChanged, rightChanged))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftStart = leftChunks[region.LeftStart];
            var leftStop = leftChunks[region.LeftStart + region.LeftCount];
            var rightStart = rightChunks[region.RightStart];
            var rightStop = rightChunks[region.RightStart + region.RightCount];
            Refine(left, leftStart, leftStop, right, rightStart, rightStop, ranges, cancellationToken);
        }

        return ranges;
    }

    /// <summary>Narrows one changed region to its differing bytes and adds the resulting ranges.</summary>
    private static void Refine(byte[] left, int leftStart, int leftEnd, byte[] right, int rightStart, int rightEnd,
        List<BinaryDiffRange> ranges, CancellationToken cancellationToken)
    {
        while (leftStart < leftEnd && rightStart < rightEnd && left[leftStart] == right[rightStart])
        {
            leftStart++;
            rightStart++;
        }

        while (leftStart < leftEnd && rightStart < rightEnd && left[leftEnd - 1] == right[rightEnd - 1])
        {
            leftEnd--;
            rightEnd--;
        }

        var leftLength = leftEnd - leftStart;
        var rightLength = rightEnd - rightStart;
        if (leftLength == 0 && rightLength == 0)
        {
            return;
        }

        if (leftLength == 0 || rightLength == 0 || leftLength + rightLength > ByteDiffLimit)
        {
            ranges.Add(BinaryDiffRange.Create(leftStart, leftLength, rightStart, rightLength));
            return;
        }

        AddByteDiff(left, leftStart, leftLength, right, rightStart, rightLength, ranges, cancellationToken);
    }

    private static void AddByteDiff(byte[] left, int leftStart, int leftLength, byte[] right, int rightStart, int rightLength,
        List<BinaryDiffRange> ranges, CancellationToken cancellationToken)
    {
        var leftBytes = new int[leftLength];
        var rightBytes = new int[rightLength];
        for (var i = 0; i < leftLength; i++)
        {
            leftBytes[i] = left[leftStart + i];
        }

        for (var i = 0; i < rightLength; i++)
        {
            rightBytes[i] = right[rightStart + i];
        }

        var leftChanged = new bool[leftLength];
        var rightChanged = new bool[rightLength];
        MyersDiff.CompareRange(leftBytes, 0, leftLength, rightBytes, 0, rightLength, leftChanged, rightChanged, cancellationToken);

        BinaryDiffRange? pending = null;
        foreach (var run in ChangedRuns(leftChanged, rightChanged))
        {
            var current = BinaryDiffRange.Create(leftStart + run.LeftStart, run.LeftCount, rightStart + run.RightStart, run.RightCount);
            if (pending is null)
            {
                pending = current;
                continue;
            }

            // A short accidental match between two changes is shown as part of one change.
            var gap = current.LeftOffset - (pending.LeftOffset + pending.LeftLength);
            if (gap < MinimumEqualRun)
            {
                pending = BinaryDiffRange.Create(
                    pending.LeftOffset,
                    current.LeftOffset + current.LeftLength - pending.LeftOffset,
                    pending.RightOffset,
                    current.RightOffset + current.RightLength - pending.RightOffset);
                continue;
            }

            ranges.Add(pending);
            pending = current;
        }

        if (pending is not null)
        {
            ranges.Add(pending);
        }
    }

    /// <summary>Groups changed elements into runs between unchanged pairs.</summary>
    internal static IEnumerable<ChangedRun> ChangedRuns(bool[] leftChanged, bool[] rightChanged)
    {
        var i = 0;
        var j = 0;
        while (i < leftChanged.Length || j < rightChanged.Length)
        {
            if (i < leftChanged.Length && j < rightChanged.Length && !leftChanged[i] && !rightChanged[j])
            {
                i++;
                j++;
                continue;
            }

            var leftStart = i;
            var rightStart = j;
            while (i < leftChanged.Length && leftChanged[i])
            {
                i++;
            }

            while (j < rightChanged.Length && rightChanged[j])
            {
                j++;
            }

            if (i == leftStart && j == rightStart)
            {
                // Only reachable if the flags do not pair up; treat everything left as changed.
                i = leftChanged.Length;
                j = rightChanged.Length;
            }

            yield return new ChangedRun(leftStart, i - leftStart, rightStart, j - rightStart);
        }
    }

    /// <summary>Returns chunk boundaries: the first is <paramref name="start"/>, the last is <paramref name="end"/>.</summary>
    private static List<int> Chunk(byte[] data, int start, int end, CancellationToken cancellationToken)
    {
        var boundaries = new List<int> { start };
        var chunkStart = start;
        ulong hash = 0;
        for (var i = start; i < end; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            var length = i + 1 - chunkStart;
            if ((length >= MinimumChunk && (hash & BoundaryMask) == 0) || length >= MaximumChunk)
            {
                boundaries.Add(i + 1);
                chunkStart = i + 1;
                hash = 0;
                if ((boundaries.Count & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        if (chunkStart < end)
        {
            boundaries.Add(end);
        }

        return boundaries;
    }

    /// <summary>Gives equal chunks on either side the same token.</summary>
    private static void Intern(byte[] left, List<int> leftChunks, byte[] right, List<int> rightChunks, int[] leftTokens, int[] rightTokens)
    {
        var ids = new Dictionary<ChunkKey, int>();
        Fill(left, leftChunks, leftTokens);
        Fill(right, rightChunks, rightTokens);

        void Fill(byte[] data, List<int> chunks, int[] tokens)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                var key = new ChunkKey(data, chunks[i], chunks[i + 1] - chunks[i]);
                if (!ids.TryGetValue(key, out var id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                }

                tokens[i] = id;
            }
        }
    }

    private static int CommonPrefix(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index + 8 <= length && BitConverter.ToUInt64(left, index) == BitConverter.ToUInt64(right, index))
        {
            index += 8;
        }

        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static int CommonSuffix(byte[] left, byte[] right, int prefix)
    {
        var length = Math.Min(left.Length, right.Length) - prefix;
        var count = 0;
        while (count < length && left[left.Length - 1 - count] == right[right.Length - 1 - count])
        {
            count++;
        }

        return count;
    }

    private static ulong[] CreateGearTable()
    {
        // A fixed SplitMix64 sequence, so chunk boundaries are the same on every run.
        var table = new ulong[256];
        var state = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            table[i] = value ^ (value >> 31);
        }

        return table;
    }

    internal readonly record struct ChangedRun(int LeftStart, int LeftCount, int RightStart, int RightCount);

    /// <summary>A chunk compared by content.</summary>
    private readonly struct ChunkKey : IEquatable<ChunkKey>
    {
        private readonly byte[] _data;
        private readonly int _offset;
        private readonly int _length;
        private readonly int _hash;

        public ChunkKey(byte[] data, int offset, int length)
        {
            _data = data;
            _offset = offset;
            _length = length;
            var hash = 2166136261u;
            for (var i = offset; i < offset + length; i++)
            {
                hash = (hash ^ data[i]) * 16777619u;
            }

            _hash = (int)hash;
        }

        public bool Equals(ChunkKey other)
        {
            if (_hash != other._hash || _length != other._length)
            {
                return false;
            }

            for (var i = 0; i < _length; i++)
            {
                if (_data[_offset + i] != other._data[other._offset + i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ChunkKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }
}
