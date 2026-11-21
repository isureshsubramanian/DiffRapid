using System;
using System.IO;
using System.Text;
using DiffRapid.Core.IO;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Streams text to <paramref name="path"/>, or to standard output when it is null, through a buffered writer.
    /// A file is written atomically: a failure part-way leaves any earlier file unchanged.
    /// </summary>
    internal static void WriteText(string? path, Action<TextWriter> write, Encoding? encoding = null)
    {
        if (path is null)
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), encoding ?? Utf8NoBom);
            write(stdout);
            return;
        }

        WriteFile(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding ?? Utf8NoBom, bufferSize: -1, leaveOpen: true);
            write(writer);
        });
    }

    /// <summary>
    /// Writes a file atomically through <see cref="AtomicFile"/>, or a device or pipe in place (see
    /// <see cref="OpenSpecial"/>).
    /// </summary>
    private static void WriteFile(string path, Action<Stream> write)
    {
        var full = Path.GetFullPath(path);
        using var special = OpenSpecial(full);
        if (special is null)
        {
            AtomicFile.Write(full, write);
            return;
        }

        write(special);
    }

    /// <summary>
    /// Opens <paramref name="full"/> for writing in place when it is a device or pipe on macOS or Linux, such as
    /// <c>-o /dev/stdout</c> or <c>-o &gt;(command)</c>: no temporary file can be created beside it or renamed
    /// over it. .NET reports no file type there, so a target counts as one when it is under <c>/dev</c>, or when it
    /// lists a length of 0 (as pipes and devices do) and opens as a stream that cannot seek, which a regular file
    /// always can. On Windows, the full path of <c>NUL</c>, <c>CON</c> and the other reserved names (in any folder)
    /// is a <c>\\.\NAME</c> device path, which has no folder for a temporary file, so it is written in place too, as
    /// is a named pipe (<c>\\.\pipe\name</c>). Returns null for everything else, which is written atomically.
    /// </summary>
    private static FileStream? OpenSpecial(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsDeviceOrPipe(full) ? new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite) : null;
        }

        var device = full.StartsWith("/dev/", StringComparison.Ordinal);
        var file = new FileInfo(full);
        if (!device && !(file.Exists && file.Length == 0))
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException) when (!device)
        {
            // A read-only empty file: the atomic writer decides, with its own message.
            return null;
        }

        if (!stream.CanSeek)
        {
            return stream;
        }

        if (device)
        {
            // /dev/stdout redirected to a regular file: the shell has already truncated (>) or not (>>) it, and may
            // have written before this command, so write after what is there instead of from offset 0.
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }

        stream.Dispose();
        return null;
    }

    // "\\.\C:\dir\file.txt" and "\\.\Volume{...}\file.txt" also live under "\\.\" but are ordinary files, which
    // must still be replaced atomically: only a bare device name or a named pipe is written in place.
    private static bool IsWindowsDeviceOrPipe(string full)
    {
        const string devicePrefix = @"\\.\";
        if (!full.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = full.Substring(devicePrefix.Length);
        return rest.Length > 0 &&
            (rest.IndexOf('\\') < 0 || rest.StartsWith(@"pipe\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes text in the encoding it was read with, keeping its byte order mark.</summary>
    private static void WriteInOriginalEncoding(string path, string text, DecodedText decoded) => WriteFile(path, stream =>
    {
        if (decoded.HasByteOrderMark)
        {
            stream.Write(decoded.Encoding.GetPreamble());
        }

        stream.Write(decoded.Encoding.GetBytes(text));
    });
}
