using System;

namespace DiffRapid.Core.Folders;

/// <summary>The binary-or-text heuristic shared by the folder scan and rename detection.</summary>
internal static class TextProbe
{
    /// <summary>Bytes inspected from the start of a file: the 8000 that git's binary check reads (xdiff FIRST_FEW_BYTES).</summary>
    internal const int Size = 8000;

    /// <summary>
    /// A NUL byte in the first <paramref name="count"/> bytes means binary, unless a UTF-16/32 byte order mark says
    /// otherwise.
    /// </summary>
    internal static bool LooksLikeText(byte[] bytes, int count)
    {
        count = Math.Min(count, Size);
        var hasUnicodeBom = (count >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            || (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF);
        return hasUnicodeBom || Array.IndexOf(bytes, (byte)0, 0, count) < 0;
    }
}
