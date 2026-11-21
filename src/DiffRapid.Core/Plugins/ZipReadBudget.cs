using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// Reads zip entries into memory within an entry count, a per-entry size and a total size, measured on the
/// inflated bytes whatever the entry headers claim, so a small crafted archive cannot exhaust memory.
/// One budget covers one archive.
/// </summary>
internal sealed class ZipReadBudget
{
    /// <summary>The most entries a zip file without Zip64 extensions can hold; real archives rarely come close.</summary>
    public const int MaxEntries = 65535;

    // 80 KB keeps the copy buffer off the large object heap, as Stream.CopyTo does.
    private const int ChunkLength = 81920;

    private readonly long _maxEntryBytes;
    private readonly long _maxTotalBytes;
    private long _used;

    /// <param name="maxEntryBytes">Entries that inflate past this are not kept.</param>
    /// <param name="maxTotalBytes">Inflating more than this across the archive fails the read.</param>
    public ZipReadBudget(long maxEntryBytes, long maxTotalBytes)
    {
        _maxEntryBytes = maxEntryBytes;
        _maxTotalBytes = maxTotalBytes;
    }

    /// <summary>The archive's entries, once their number is known to be within <see cref="MaxEntries"/>.</summary>
    /// <exception cref="InvalidDataException">The archive has more entries than <see cref="MaxEntries"/>.</exception>
    public static IReadOnlyCollection<ZipArchiveEntry> Entries(ZipArchive archive)
    {
        var entries = archive.Entries;
        if (entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The archive has {entries.Count} entries; at most {MaxEntries} are read.");
        }

        return entries;
    }

    /// <summary>
    /// Inflates one entry. Returns null when it inflates past the per-entry size; the bytes inflated until then
    /// still count towards the total. Worst case per call: the per-entry size in memory, twice while copying out.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry is damaged, or the archive's total budget is used up.</exception>
    /// <exception cref="System.OperationCanceledException">The token was cancelled.</exception>
    public byte[]? Read(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkLength];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > _maxTotalBytes - _used)
            {
                throw new InvalidDataException($"The archive inflates to more than {_maxTotalBytes} bytes in all.");
            }

            _used += read;
            if (read > _maxEntryBytes - buffer.Length)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
