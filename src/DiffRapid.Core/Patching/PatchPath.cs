using System.Collections.Generic;
using System.Text;

namespace DiffRapid.Core.Patching;

/// <summary>Git's C-style quoting of patch paths, so a name with a tab, newline or quote stays one field.</summary>
internal static class PatchPath
{
    /// <summary>
    /// Returns <paramref name="path"/> unchanged when it is safe to write bare, otherwise in double quotes with
    /// \t \n \r \" \\ (and three-digit octal for other control characters), as git writes it. Characters outside
    /// ASCII are kept as they are, like git with core.quotepath=false.
    /// </summary>
    internal static string Quote(string path)
    {
        if (!NeedsQuotes(path))
        {
            return path;
        }

        var quoted = new StringBuilder(path.Length + 8).Append('"');
        foreach (var character in path)
        {
            switch (character)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\t': quoted.Append("\\t"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                default:
                    if (character < ' ' || character == '\x7f')
                    {
                        quoted.Append('\\').Append(System.Convert.ToString((int)character, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        quoted.Append(character);
                    }

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Reads a quoted path starting at the opening quote of <paramref name="value"/>. Octal escapes are bytes of the
    /// UTF-8 name, as git writes them. Returns the path and the index after the closing quote, or null when the
    /// quotes or an escape are invalid, or the path holds a NUL character (no file system allows one in a name).
    /// </summary>
    internal static (string Path, int End)? Unquote(string value)
    {
        var text = new StringBuilder(value.Length);
        var octets = new List<byte>();
        void Flush()
        {
            text.Append(Encoding.UTF8.GetString(octets.ToArray()));
            octets.Clear();
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                Flush();
                var path = text.ToString();
                return path.IndexOf('\0') >= 0 ? null : (path, index + 1);
            }

            if (character != '\\')
            {
                Flush();
                text.Append(character);
                continue;
            }

            if (++index >= value.Length)
            {
                return null;
            }

            var escape = value[index];
            var simple = escape switch
            {
                'a' => '\a', 'b' => '\b', 't' => '\t', 'n' => '\n', 'v' => '\v', 'f' => '\f', 'r' => '\r', '"' => '"', '\\' => '\\',
                _ => (char?)null,
            };
            if (simple is { } unescaped)
            {
                Flush();
                text.Append(unescaped);
                continue;
            }

            // Exactly three octal digits, at most \377: one byte of the UTF-8 name.
            if (index + 2 >= value.Length || escape is < '0' or > '3' || !IsOctal(value[index + 1]) || !IsOctal(value[index + 2]))
            {
                return null;
            }

            octets.Add((byte)(((escape - '0') << 6) | ((value[index + 1] - '0') << 3) | (value[index + 2] - '0')));
            index += 2;
        }

        return null;
    }

    private static bool IsOctal(char character) => character is >= '0' and <= '7';

    private static bool NeedsQuotes(string path)
    {
        foreach (var character in path)
        {
            if (character < ' ' || character == '\x7f' || character == '"' || character == '\\')
            {
                return true;
            }
        }

        return false;
    }
}
