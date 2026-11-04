using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiffRapid.Core.History;

public sealed partial class LocalHistoryStore
{
    /// <summary>Applies age, per-file count and total compressed blob limits, then removes unreferenced blobs.</summary>
    public void Prune(CancellationToken cancellationToken = default)
    {
        using var storeLock = AcquireLock(cancellationToken);
        var indexes = LoadRetainedIndexes(cancellationToken);
        ApplySizeLimit(indexes, cancellationToken);
        var referencedHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = index.Entries.Where(candidate => candidate.Keep).Select(candidate => candidate.Entry).ToArray();
            if (entries.Length == 0)
            {
                File.Delete(index.Path);
            }
            else
            {
                WriteIndex(index.Path, entries.OrderBy(entry => entry.TimestampUtc), cancellationToken);
            }

            foreach (var entry in entries)
            {
                referencedHashes.Add(entry.Hash);
            }
        }

        DeleteOrphans(referencedHashes, cancellationToken);
    }

    private List<RetainedIndex> LoadRetainedIndexes(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = _options.MaxAge >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - _options.MaxAge;
        var indexes = new List<RetainedIndex>();
        foreach (var indexPath in Directory.EnumerateFiles(_indexFolder, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = NewestFirst(ReadIndex(indexPath, cancellationToken))
                .Where(entry => entry.TimestampUtc >= cutoff)
                .Take(_options.MaxEntriesPerFile)
                .Reverse()
                .Select(entry => new RetainedEntry(entry))
                .ToArray();
            indexes.Add(new RetainedIndex(indexPath, entries));
        }

        return indexes;
    }

    private void ApplySizeLimit(IEnumerable<RetainedIndex> indexes, CancellationToken cancellationToken)
    {
        var entries = indexes.SelectMany(index => index.Entries).ToArray();
        var references = new Dictionary<string, BlobReferences>(StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var candidate in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!references.TryGetValue(candidate.Entry.Hash, out var blob))
            {
                var path = BlobPath(candidate.Entry.Hash);
                blob = new BlobReferences(File.Exists(path) ? new FileInfo(path).Length : 0);
                references.Add(candidate.Entry.Hash, blob);
                totalSize += blob.Size;
            }

            blob.Count++;
        }

        foreach (var candidate in entries.OrderBy(candidate => candidate.Entry.TimestampUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalSize <= _options.MaxTotalSize)
            {
                break;
            }

            candidate.Keep = false;
            var blob = references[candidate.Entry.Hash];
            blob.Count--;
            if (blob.Count == 0)
            {
                totalSize -= blob.Size;
            }
        }
    }

    private void DeleteOrphans(HashSet<string> referencedHashes, CancellationToken cancellationToken)
    {
        foreach (var blobPath in Directory.EnumerateFiles(_blobsFolder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!referencedHashes.Contains(Path.GetFileName(blobPath)))
            {
                File.Delete(blobPath);
            }
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(_indexFolder, "*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(temporaryPath);
        }
    }

    private sealed record RetainedIndex(string Path, RetainedEntry[] Entries);

    private sealed class RetainedEntry(LocalHistoryEntry entry)
    {
        public LocalHistoryEntry Entry { get; } = entry;

        public bool Keep { get; set; } = true;
    }

    private sealed class BlobReferences(long size)
    {
        public long Size { get; } = size;

        public int Count { get; set; }
    }
}
