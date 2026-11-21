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

    /// <summary>"1 file", "2 files": the count with thousands separators and the noun in the right number.</summary>
    public static string Plural(int count, string singular) => $"{count:N0} {(count == 1 ? singular : singular + "s")}";

    /// <summary>"12 ms" under a second, "1.5 s" above.</summary>
    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000 ? $"{Math.Max(1, (int)elapsed.TotalMilliseconds)} ms" : $"{elapsed.TotalSeconds:0.0} s";

    /// <summary>The last part of a folder path ("C:\src\App\" gives "App"); a drive root stays as it is.</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// Whether the first bytes of a file look binary: a NUL byte, unless the file starts with a UTF-16 or UTF-32 byte
    /// order mark (little or big endian), whose text has NULs too.
    /// </summary>
    public static bool LooksBinary(byte[] buffer, int read)
    {
        var utf16Or32Little = read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE;
        var utf16Big = read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF;
        var utf32Big = read >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF;
        return !utf16Or32Little && !utf16Big && !utf32Big && Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

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
