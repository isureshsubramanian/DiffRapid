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

    /// <summary>
    /// The longest archive opened (an estimate: just past nuget.org's 250 MB package limit, and past ordinary jars
    /// and extensions). ZipArchive builds an entry for every central directory record before <see cref="Entries"/>
    /// can count them, and it reads records until they stop rather than up to the count the end record declares,
    /// so reading that count first would not bound the work; the input length is capped instead. Worst case:
    /// 256 MiB / 46 bytes per record (the record's fixed part) is about 5.8 million entries built, then rejected.
    /// </summary>
    public const long MaxArchiveBytes = 256L * 1024 * 1024;

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

    /// <summary>
    /// Opens an archive for reading once its length is known to be within <see cref="MaxArchiveBytes"/>; the stream
    /// is left open. A stream that cannot seek is copied to memory, as ZipArchive would, but only up to that length.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is longer than <see cref="MaxArchiveBytes"/>, or is not a zip archive.</exception>
    public static ZipArchive Open(Stream input)
    {
        if (input.CanSeek)
        {
            CheckArchiveLength(input.Length);
            return new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }

        var copy = new MemoryStream();
        var chunk = new byte[ChunkLength];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            CheckArchiveLength(copy.Length + read);
            copy.Write(chunk, 0, read);
        }

        copy.Position = 0;
        return new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static void CheckArchiveLength(long length)
    {
        if (length > MaxArchiveBytes)
        {
            throw new InvalidDataException($"The archive is longer than {MaxArchiveBytes} bytes.");
        }
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
