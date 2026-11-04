using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Patching;

/// <summary>A line in a unified patch.</summary>
/// <param name="Kind">Context (space), deletion (-), or insertion (+).</param>
/// <param name="Text">The content without its line terminator.</param>
/// <param name="NoNewline">Whether the source side or sides lack a final newline.</param>
public sealed record PatchLine(char Kind, string Text, bool NoNewline = false);

/// <summary>A unified patch hunk with one-based nonempty ranges.</summary>
/// <param name="OldStart">The old range start, or preceding line for an empty range.</param>
/// <param name="OldCount">The old range length.</param>
/// <param name="NewStart">The new range start, or preceding line for an empty range.</param>
/// <param name="NewCount">The new range length.</param>
/// <param name="Lines">The context and edits in order.</param>
public sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<PatchLine> Lines);

/// <summary>A file change; null paths represent /dev/null.</summary>
/// <param name="OldPath">The source path, or null for an addition.</param>
/// <param name="NewPath">The destination path, or null for a deletion.</param>
/// <param name="Hunks">The file's text changes.</param>
public sealed record FilePatch(string? OldPath, string? NewPath, IReadOnlyList<PatchHunk> Hunks)
{
    /// <summary>Whether the patch describes binary content that cannot be applied as text.</summary>
    public bool IsBinary { get; init; }
}

/// <summary>A collection of unified file patches.</summary>
/// <param name="Files">The file changes in patch order.</param>
public sealed record Patch(IReadOnlyList<FilePatch> Files)
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$", RegexOptions.Compiled);
    private static readonly Regex GitHeader = new("^diff --git (\"(?:\\\\.|[^\"])*\"|a/.*?) (\"(?:\\\\.|[^\"])*\"|b/.*)$", RegexOptions.Compiled);

    /// <summary>Parses plain unified diffs and git text diffs.</summary>
    public static Patch Parse(string text)
    {
        var input = TextDocument.Parse(text);
        var files = new List<FilePatch>();
        for (var index = 0; index < input.LineCount; index++)
        {
            var header = input[index].Content;
            if (header.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                ParseGitHeader(input, ref index, files);
            }
            else if (header.StartsWith("--- ", StringComparison.Ordinal))
            {
                files.Add(ParseFileHeader(input, ref index));
            }
        }

        return new Patch(files.AsReadOnly());
    }

    private static void ParseGitHeader(TextDocument input, ref int index, List<FilePatch> files)
    {
        var paths = GitHeader.Match(input[index].Content);
        var end = index + 1;
        var binary = false;
        var added = false;
        var deleted = false;
        var renamed = false;
        while (end < input.LineCount && !input[end].Content.StartsWith("diff --git ", StringComparison.Ordinal)
            && !input[end].Content.StartsWith("--- ", StringComparison.Ordinal))
        {
            var metadata = input[end].Content;
            binary |= metadata.StartsWith("Binary files ", StringComparison.Ordinal) || metadata == "GIT binary patch";
            added |= metadata.StartsWith("new file mode ", StringComparison.Ordinal);
            deleted |= metadata.StartsWith("deleted file mode ", StringComparison.Ordinal);
            renamed |= metadata.StartsWith("rename from ", StringComparison.Ordinal);
            end++;
        }

        // Without ---/+++ lines the header alone describes the change: an empty file added or deleted, a binary
        // change, or a rename that keeps the content (similarity index 100%).
        var hasFileHeader = end < input.LineCount && input[end].Content.StartsWith("--- ", StringComparison.Ordinal);
        if (binary || ((added || deleted || renamed) && !hasFileHeader))
        {
            if (!paths.Success)
            {
                throw Error(index + 1, "invalid git file header");
            }

            files.Add(new FilePatch(added ? null : ParsePath(paths.Groups[1].Value, index + 1),
                deleted ? null : ParsePath(paths.Groups[2].Value, index + 1), Array.Empty<PatchHunk>()) { IsBinary = binary });
            index = end - 1;
        }
    }

    private static FilePatch ParseFileHeader(TextDocument input, ref int index)
    {
        var oldPath = ParsePath(input[index].Content.Substring(4), index + 1);
        index++;
        if (index >= input.LineCount || !input[index].Content.StartsWith("+++ ", StringComparison.Ordinal))
        {
            throw Error(index + 1, "expected +++ file header");
        }

        var newPath = ParsePath(input[index].Content.Substring(4), index + 1);
        var hunks = new List<PatchHunk>();
        while (index + 1 < input.LineCount && input[index + 1].Content.StartsWith("@@", StringComparison.Ordinal))
        {
            index++;
            var hunk = ParseHunkHeader(input[index].Content, index + 1);
            var lines = ParseHunkBody(input, ref index, hunk.OldCount, hunk.NewCount);
            hunks.Add(hunk with { Lines = lines });
        }

        return new FilePatch(oldPath, newPath, hunks.AsReadOnly());
    }

    private static PatchHunk ParseHunkHeader(string header, int line)
    {
        var match = HunkHeader.Match(header);
        if (!match.Success)
        {
            throw Error(line, "invalid hunk header");
        }

        var oldStart = ParseNumber(match, 1, 0, line);
        var oldCount = ParseNumber(match, 2, 1, line);
        var newStart = ParseNumber(match, 3, 0, line);
        var newCount = ParseNumber(match, 4, 1, line);
        if ((oldCount > 0 && oldStart == 0) || (newCount > 0 && newStart == 0))
        {
            throw Error(line, "nonempty hunk ranges must start at line 1 or later");
        }

        return new PatchHunk(oldStart, oldCount, newStart, newCount, Array.Empty<PatchLine>());
    }

    private static int ParseNumber(Match match, int group, int fallback, int line)
    {
        if (!match.Groups[group].Success)
        {
            return fallback;
        }

        if (!int.TryParse(match.Groups[group].Value, out var number))
        {
            throw Error(line, "hunk range exceeds the supported integer range");
        }

        return number;
    }

    private static IReadOnlyList<PatchLine> ParseHunkBody(TextDocument input, ref int index, int oldCount, int newCount)
    {
        var lines = new List<PatchLine>();
        var oldLines = 0;
        var newLines = 0;
        while (index + 1 < input.LineCount)
        {
            var body = input[index + 1].Content;
            if (body == "\\ No newline at end of file")
            {
                if (lines.Count == 0 || lines[lines.Count - 1].NoNewline)
                {
                    throw Error(index + 2, "newline marker must follow an unmarked hunk line");
                }

                lines[lines.Count - 1] = lines[lines.Count - 1] with { NoNewline = true };
                index++;
                continue;
            }

            if (oldLines == oldCount && newLines == newCount)
            {
                break;
            }

            if (body.Length == 0 || (body[0] != ' ' && body[0] != '-' && body[0] != '+'))
            {
                throw CountError(index + 2, oldCount, newCount, oldLines, newLines);
            }

            if (body[0] != '+')
            {
                oldLines++;
            }

            if (body[0] != '-')
            {
                newLines++;
            }

            if (oldLines > oldCount || newLines > newCount)
            {
                throw CountError(index + 2, oldCount, newCount, oldLines, newLines);
            }

            lines.Add(new PatchLine(body[0], body.Substring(1)));
            index++;
        }

        if (oldLines != oldCount || newLines != newCount)
        {
            throw CountError(index + 2, oldCount, newCount, oldLines, newLines);
        }

        if (index + 1 < input.LineCount && input[index + 1].Content.Length > 0
            && " +-".IndexOf(input[index + 1].Content[0]) >= 0
            && !input[index + 1].Content.StartsWith("--- ", StringComparison.Ordinal))
        {
            throw Error(index + 2, "extra hunk body line after declared counts");
        }

        return lines.AsReadOnly();
    }

    private static FormatException CountError(int line, int oldCount, int newCount, int oldLines, int newLines)
        => oldLines != oldCount
            ? Error(line, $"hunk says {oldCount} old lines but has {oldLines}")
            : Error(line, $"hunk says {newCount} new lines but has {newLines}");

    private static FormatException Error(int line, string message) => new($"Patch line {line}: {message}");

    private static string? ParsePath(string value, int line)
    {
        var tab = value.IndexOf('\t');
        if (tab >= 0)
        {
            value = value.Substring(0, tab);
        }

        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            if (!value.EndsWith("\"", StringComparison.Ordinal) || value.Length < 2)
            {
                throw Error(line, "invalid quoted patch path");
            }

            try
            {
                value = Regex.Unescape(value.Substring(1, value.Length - 2));
            }
            catch (ArgumentException)
            {
                throw Error(line, "invalid escape in quoted patch path");
            }
        }

        if (value == "/dev/null")
        {
            return null;
        }

        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value.Substring(2) : value;
    }
}
