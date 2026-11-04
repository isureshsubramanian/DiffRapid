using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DiffRapid.Core.History;

public sealed partial class LocalHistoryStore
{
    private LocalHistoryEntry? SaveSnapshot(string fullPath, Stream source, string label, string? note, CancellationToken cancellationToken)
    {
        if (source.Length > _options.MaxFileSize || (!_options.IncludeBinary && IsBinary(source, cancellationToken)))
        {
            return null;
        }

        var temporaryPath = TemporaryPath(_blobsFolder);
        try
        {
            var content = Compress(source, temporaryPath, cancellationToken);
            if (content is null)
            {
                return null;
            }

            var indexPath = IndexPath(fullPath);
            var latest = NewestFirst(ReadIndex(indexPath, cancellationToken)).FirstOrDefault();
            if (latest?.Hash == content.Value.Hash)
            {
                return null;
            }

            var blobPath = BlobPath(content.Value.Hash);
            if (!File.Exists(blobPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                File.Move(temporaryPath, blobPath);
            }

            var entry = new LocalHistoryEntry(DateTimeOffset.UtcNow, content.Value.Hash, content.Value.Size, label, note, fullPath);
            AppendEntry(indexPath, entry, cancellationToken);
            return entry;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private (string Hash, long Size)? Compress(Stream source, string temporaryPath, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        var buffer = new byte[BufferSize];
        long size = 0;
        using (var compressed = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                size += count;
                if (size > _options.MaxFileSize)
                {
                    return null;
                }

                hash.TransformBlock(buffer, 0, count, null, 0);
                compressed.Write(buffer, 0, count);
            }
        }

        hash.TransformFinalBlock(buffer, 0, 0);
        output.Flush(flushToDisk: true);
        return (Hex(hash.Hash!), size);
    }

    private static bool IsBinary(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[BinaryProbeLength];
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source.Read(buffer, total, buffer.Length - total);
            if (count == 0)
            {
                break;
            }

            for (var index = total; index < total + count; index++)
            {
                if (buffer[index] == 0)
                {
                    return true;
                }
            }

            total += count;
        }

        source.Position = 0;
        return false;
    }

    private bool WriteBuffer(Stream output, string text, Encoding encoding, CancellationToken cancellationToken)
    {
        using (var writer = new StreamWriter(output, encoding, BufferSize, leaveOpen: true))
        {
            var characters = new char[4096];
            for (var offset = 0; offset < text.Length; offset += characters.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(characters.Length, text.Length - offset);
                text.CopyTo(offset, characters, 0, count);
                writer.Write(characters, 0, count);
                if (output.Length > _options.MaxFileSize)
                {
                    return false;
                }
            }
        }

        return output.Length <= _options.MaxFileSize;
    }

    private void CopyBlob(LocalHistoryEntry entry, Stream output, CancellationToken cancellationToken)
    {
        using var input = new FileStream(BlobPath(entry.Hash), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var compressed = new GZipStream(input, CompressionMode.Decompress);
        using var hash = SHA256.Create();
        var buffer = new byte[BufferSize];
        long size = 0;
        int count;
        while ((count = compressed.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            size += count;
            if (size > entry.Size)
            {
                throw new InvalidDataException("The history blob exceeds its recorded size.");
            }

            hash.TransformBlock(buffer, 0, count, null, 0);
            output.Write(buffer, 0, count);
        }

        hash.TransformFinalBlock(buffer, 0, 0);
        if (size != entry.Size || Hex(hash.Hash!) != entry.Hash)
        {
            throw new InvalidDataException("The history blob does not match its recorded content.");
        }
    }
}
