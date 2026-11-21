using System;
using System.IO;

namespace DiffRapid.Core.IO;

/// <summary>
/// Writes a file so that readers see either the old content or the complete new content, never a part:
/// the content goes to a temporary file in the same folder, is flushed to disk, and then replaces the target.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Runs <paramref name="write"/> against a temporary file next to <paramref name="path"/> and, when it returns,
    /// moves the result over the target. When <paramref name="write"/> throws, the target is unchanged and the
    /// temporary file is deleted. A read-only target is replaced only when <paramref name="replaceReadOnly"/> is set;
    /// otherwise it is refused before anything is written, on every platform. The replaced file's Unix permission bits
    /// carry over to the new file. When the target is a symbolic link, the file it finally points to is replaced and
    /// the link stays a link, as an ordinary write through the link would behave.
    /// <paramref name="beforeReplace"/>, when given, runs with the temporary file's path once it is complete and
    /// closed (to verify it or set its times); when it throws, the target is likewise unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty or has no folder part.</exception>
    /// <exception cref="IOException">The file could not be written or replaced, or a link chain is circular.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or a read-only target cannot be written.</exception>
    public static void Write(string path, Action<Stream> write, bool replaceReadOnly = false, Action<string>? beforeReplace = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("A file path is required.", nameof(path));
        }

        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder))
        {
            throw new ArgumentException("The path has no folder.", nameof(path));
        }

        // Replacing a link would turn it into a regular file; write to the file it points to instead.
        if (FileModes.FinalLinkTarget(fullPath) is { } linked)
        {
            fullPath = linked;
            folder = Path.GetDirectoryName(fullPath)!;
        }

        // Unix rename() replaces a read-only file silently, so the rule is checked here, before any temporary file.
        if (!replaceReadOnly && File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
        {
            throw new UnauthorizedAccessException("The file is read-only: " + fullPath);
        }

        Directory.CreateDirectory(folder);
        // A dot prefix keeps the temporary file out of most listings; the random part avoids clashes between writers.
        // The name is short and fixed-length, so a target name near the 255-byte limit still leaves room for it.
        var temporary = Path.Combine(folder, ".~dr" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke(temporary);

            if (File.Exists(fullPath))
            {
                if (replaceReadOnly)
                {
                    var attributes = File.GetAttributes(fullPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                // A rename gives the new file the temporary file's mode; keep the replaced file's (a script stays executable).
                if (FileModes.Get(fullPath) is { } mode)
                {
                    FileModes.Set(temporary, mode);
                }

                File.Replace(temporary, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, fullPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // The original error (or the completed write) matters more than a leftover temporary file.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Writes <paramref name="bytes"/> atomically; see <see cref="Write(string, Action{Stream}, bool, Action{string}?)"/>.</summary>
    public static void WriteAllBytes(string path, byte[] bytes, bool replaceReadOnly = false)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        Write(path, stream => stream.Write(bytes, 0, bytes.Length), replaceReadOnly);
    }
}
