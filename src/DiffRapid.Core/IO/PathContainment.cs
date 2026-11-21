using System;
using System.IO;

namespace DiffRapid.Core.IO;

/// <summary>Checks that a full path stays inside a root folder, the same way for every caller.</summary>
internal static class PathContainment
{
    /// <summary>Case-insensitive on Windows, whose file systems ignore case by default; ordinal elsewhere.</summary>
    internal static StringComparison Comparison =>
        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns <paramref name="fullPath"/> relative to <paramref name="root"/> (with the platform's separators) when
    /// it lies inside the root, or null when it does not. Both must already be full paths
    /// (<see cref="Path.GetFullPath(string)"/>), so ".." segments are gone before the comparison.
    /// </summary>
    internal static string? RelativeInside(string root, string fullPath)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, Comparison) ? fullPath.Substring(prefix.Length) : null;
    }

    /// <summary>
    /// Whether an existing entry between <paramref name="root"/> (excluded) and <paramref name="fullPath"/> (included)
    /// is a link or junction, which could redirect a contained path outside the root. Attributes are read without
    /// following the entry itself, so a dangling link counts too. The answer can change after it is given; callers
    /// check immediately before touching the path.
    /// </summary>
    /// <exception cref="IOException">An entry's attributes could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">An entry's attributes could not be read.</exception>
    internal static bool PassesThroughLink(string root, string fullPath)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = fullPath; current is not null && current.Length > trimmedRoot.Length; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
