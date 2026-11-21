using System;
using System.IO;
using System.Linq;
using System.Text;
using DiffRapid.Core.IO;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Small text and file helpers shared by the comparison views.</summary>
internal static class UiText
{
    // Characters per StreamWriter buffer for reports and patches (an estimate: the 4 KiB FileStream default).
    private const int WriterBufferSize = 4096;

    /// <summary>The name with each character a file name cannot hold replaced by '_'.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    /// <summary>
    /// Writes text to <paramref name="path"/> through a temporary file (see <see cref="AtomicFile"/>), so a failure
    /// never leaves a half-written report or patch. Safe to call from a background thread.
    /// </summary>
    public static void WriteText(string path, Encoding encoding, Action<StreamWriter> write) =>
        AtomicFile.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding, WriterBufferSize, leaveOpen: true);
            write(writer);
        });
}
