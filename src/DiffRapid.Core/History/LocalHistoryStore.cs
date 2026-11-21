using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using DiffRapid.Core.IO;

namespace DiffRapid.Core.History;

/// <summary>Stores gzip-compressed, content-addressed snapshots with one JSON-lines index per file.</summary>
public sealed partial class LocalHistoryStore
{
    private const int BufferSize = 64 * 1024;
    private const int BinaryProbeLength = 8000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _rootFolder;
    private readonly string _blobsFolder;
    private readonly string _indexFolder;
    private readonly LocalHistoryOptions _options;

    /// <summary>Opens or creates a store. Retention limits are applied only by <see cref="Prune"/>.</summary>
    public LocalHistoryStore(string rootFolder, LocalHistoryOptions options)
    {
        _rootFolder = NormalizePath(rootFolder);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _blobsFolder = Path.Combine(_rootFolder, "blobs");
        _indexFolder = Path.Combine(_rootFolder, "index");
        Directory.CreateDirectory(_blobsFolder);
        Directory.CreateDirectory(_indexFolder);
    }

    /// <summary>Captures a file, or returns null for unchanged, oversized or excluded binary content.</summary>
    public LocalHistoryEntry? Snapshot(string path, string label, string? note = null, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        ValidateLabel(label);
        using var storeLock = AcquireLock(cancellationToken);
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        return SaveSnapshot(fullPath, source, label, note, cancellationToken);
    }

    /// <summary>Captures an unsaved buffer using the encoding's preamble and the same exclusion rules as a file.</summary>
    public LocalHistoryEntry? SnapshotText(string path, string text, Encoding encoding, string label, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        ValidateLabel(label);
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (encoding is null)
        {
            throw new ArgumentNullException(nameof(encoding));
        }

        using var storeLock = AcquireLock(cancellationToken);
        var temporaryPath = TemporaryPath(_blobsFolder);
        try
        {
            using var source = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize);
            if (!WriteBuffer(source, text, encoding, cancellationToken))
            {
                return null;
            }

            source.Position = 0;
            return SaveSnapshot(fullPath, source, label, null, cancellationToken);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> may have snapshots: its index file exists. It takes no lock and reads no file,
    /// so it suits the UI thread; <see cref="List"/> (which waits for the store lock) returns the entries.
    /// </summary>
    public bool HasHistory(string path) => File.Exists(IndexPath(NormalizePath(path)));

    /// <summary>Lists snapshots newest first, skipping malformed index lines.</summary>
    public IReadOnlyList<LocalHistoryEntry> List(string path, CancellationToken cancellationToken = default)
    {
        var indexPath = IndexPath(NormalizePath(path));
        using var storeLock = AcquireLock(cancellationToken);
        return NewestFirst(ReadIndex(indexPath, cancellationToken)).ToArray();
    }

    /// <summary>Reads and verifies the original uncompressed content of a snapshot.</summary>
    public byte[] Read(LocalHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        if (entry.Size > int.MaxValue)
        {
            throw new IOException("The snapshot is too large to read into a byte array. Use Restore instead.");
        }

        using var storeLock = AcquireLock(cancellationToken);
        using var output = new MemoryStream();
        CopyBlob(entry, output, cancellationToken);
        return output.ToArray();
    }

    /// <summary>Verifies a snapshot into a temporary sibling, then atomically replaces or creates the target.</summary>
    public void Restore(LocalHistoryEntry entry, string path, CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        var fullPath = NormalizePath(path);
        using var storeLock = AcquireLock(cancellationToken);
        AtomicFile.Write(fullPath, output => CopyBlob(entry, output, cancellationToken),
            beforeReplace: _ => cancellationToken.ThrowIfCancellationRequested());
    }

    /// <summary>Moves history to a new path, merging destination history and preserving paths recorded at capture time.</summary>
    public void Rename(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        var oldIndexPath = IndexPath(NormalizePath(oldPath));
        var newIndexPath = IndexPath(NormalizePath(newPath));
        using var storeLock = AcquireLock(cancellationToken);
        if (oldIndexPath == newIndexPath || !File.Exists(oldIndexPath))
        {
            return;
        }

        var entries = ReadIndex(newIndexPath, cancellationToken).Concat(ReadIndex(oldIndexPath, cancellationToken));
        WriteIndex(newIndexPath, entries.Distinct().OrderBy(entry => entry.TimestampUtc), cancellationToken);
        File.Delete(oldIndexPath);
    }

    private FileStream AcquireLock(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file in place so every process locks the same file.
                return new FileStream(Path.Combine(_rootFolder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellationToken.WaitHandle.WaitOne(25);
            }
        }
    }

    private List<LocalHistoryEntry> ReadIndex(string indexPath, CancellationToken cancellationToken)
    {
        var entries = new List<LocalHistoryEntry>();
        if (!File.Exists(indexPath))
        {
            return entries;
        }

        using var reader = new StreamReader(indexPath, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ParseEntry(line) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private void AppendEntry(string indexPath, LocalHistoryEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new FileStream(indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (output.Length > 0)
        {
            output.Position = output.Length - 1;
            if (output.ReadByte() != '\n')
            {
                output.WriteByte((byte)'\n');
            }
        }

        using (var writer = new StreamWriter(output, new UTF8Encoding(false), BufferSize, leaveOpen: true))
        {
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
        }

        output.Flush(flushToDisk: true);
    }

    private static void WriteIndex(string indexPath, IEnumerable<LocalHistoryEntry> entries, CancellationToken cancellationToken)
    {
        AtomicFile.Write(
            indexPath,
            output =>
            {
                using var writer = new StreamWriter(output, new UTF8Encoding(false), BufferSize, leaveOpen: true);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                }
            },
            beforeReplace: _ => cancellationToken.ThrowIfCancellationRequested());
    }

    /// <summary>
    /// The most recently written valid entry, which is the last parseable line (appends add the newest entry; a
    /// rewrite orders by time). The file is read from its end in growing windows, so the cost depends on the last
    /// entries rather than the whole history; at worst, with no valid line, about 4/3 of the file is read.
    /// </summary>
    private static LocalHistoryEntry? ReadLatest(string indexPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            return null;
        }

        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Estimate: an index line is a few hundred bytes, so 4 KB usually holds the last entry several times over.
        for (var window = 4096L; ; window *= 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Math.Max(0, stream.Length - window);
            var bytes = new byte[stream.Length - start];
            stream.Position = start;
            var count = 0;
            int read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0)
            {
                count += read;
            }

            // Unless the window reaches the start of the file, its first line may be cut and is not trusted.
            var lines = Encoding.UTF8.GetString(bytes, 0, count).Split('\n');
            for (var index = lines.Length - 1; index >= (start == 0 ? 0 : 1); index--)
            {
                if (ParseEntry(lines[index].TrimEnd('\r')) is { } entry)
                {
                    return entry;
                }
            }

            if (start == 0)
            {
                return null;
            }
        }
    }

    private static LocalHistoryEntry? ParseEntry(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<LocalHistoryEntry>(line, JsonOptions);
            return IsValidEntry(entry) ? entry : null;
        }
        catch (JsonException)
        {
            // A torn or damaged line must not hide the other snapshots.
            return null;
        }
    }

    private string IndexPath(string fullPath)
    {
        var key = Path.DirectorySeparatorChar == '\\' ? fullPath.ToUpperInvariant() : fullPath;
        using var hash = SHA256.Create();
        return Path.Combine(_indexFolder, Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(key)))) + ".jsonl";
    }

    private string BlobPath(string hash) => Path.Combine(_blobsFolder, hash.Substring(0, 2), hash);

    private static string TemporaryPath(string folder) => Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A nonempty path is required.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static IOrderedEnumerable<LocalHistoryEntry> NewestFirst(IEnumerable<LocalHistoryEntry> entries) =>
        entries.Reverse().OrderByDescending(entry => entry.TimestampUtc);

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    private static void ValidateOptions(LocalHistoryOptions options)
    {
        if (options.MaxFileSize < 0 || options.MaxAge < TimeSpan.Zero || options.MaxEntriesPerFile < 0 || options.MaxTotalSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "History limits must be nonnegative.");
        }
    }

    private static void ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("A nonempty snapshot label is required.", nameof(label));
        }
    }

    private static void ValidateEntry(LocalHistoryEntry entry)
    {
        if (!IsValidEntry(entry))
        {
            throw new ArgumentException("Invalid local history entry.", nameof(entry));
        }
    }

    private static bool IsValidEntry(LocalHistoryEntry? entry) =>
        entry != null && IsHash(entry.Hash) && entry.Size >= 0 && entry.TimestampUtc != default &&
        entry.TimestampUtc.Offset == TimeSpan.Zero && !string.IsNullOrWhiteSpace(entry.Label) &&
        !string.IsNullOrWhiteSpace(entry.OriginalPath) && Path.IsPathRooted(entry.OriginalPath);

    private static bool IsHash(string? hash)
    {
        if (hash is null || hash.Length != 64)
        {
            return false;
        }

        foreach (var character in hash)
        {
            if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
