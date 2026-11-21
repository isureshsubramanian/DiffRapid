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

    /// <summary>Writes a file atomically through <see cref="AtomicFile"/>.</summary>
    private static void WriteFile(string path, Action<Stream> write) => AtomicFile.Write(Path.GetFullPath(path), write);

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
