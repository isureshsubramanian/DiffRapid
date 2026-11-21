using System.Globalization;
using System.Text;

namespace DiffRapid.Cli;

/// <summary>
/// Makes text from files, repositories and AI services safe to print: C0 and C1 control characters (ESC included)
/// and DEL become <c>\xNN</c>, so the text cannot move the cursor, change colours, set the window title or run
/// other terminal sequences. Tabs are kept.
/// </summary>
/// <remarks>
/// Applied always, not only when stdout is a terminal, to file names, metadata, table cells, structured values and
/// AI replies. Diff bodies and files written with -o keep their content unchanged.
/// </remarks>
internal static class TerminalText
{
    /// <summary>Escapes control characters; with <paramref name="keepLineFeeds"/>, '\n' stays a line break.</summary>
    public static string Escape(string text, bool keepLineFeeds = false)
    {
        var index = 0;
        while (index < text.Length && !NeedsEscape(text[index], keepLineFeeds))
        {
            index++;
        }

        if (index == text.Length)
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length + 16).Append(text, 0, index);
        for (; index < text.Length; index++)
        {
            var character = text[index];
            if (NeedsEscape(character, keepLineFeeds))
            {
                escaped.Append("\\x").Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }

    private static bool NeedsEscape(char character, bool keepLineFeeds) =>
        character != '\t' && !(keepLineFeeds && character == '\n')
        && (character < 0x20 || (character >= 0x7F && character <= 0x9F));
}
