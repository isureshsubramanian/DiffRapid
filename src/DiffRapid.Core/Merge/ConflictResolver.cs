using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Merge;

/// <summary>Why a conflict could be resolved without asking anyone.</summary>
public enum SimpleResolutionKind
{
    /// <summary>Both sides made the same change; the left lines are taken.</summary>
    SameChange,

    /// <summary>The left side changed only whitespace, so the right side's change is taken.</summary>
    LeftWhitespaceOnly,

    /// <summary>The right side changed only whitespace, so the left side's change is taken.</summary>
    RightWhitespaceOnly,

    /// <summary>The sides changed different lines of the conflict; both changes are combined.</summary>
    DifferentLines,
}

/// <summary>
/// One conflict resolved automatically. <see cref="Chunk"/> is the session's chunk index, or the zero-based
/// conflict number when resolving conflict text.
/// </summary>
public sealed record SimpleResolution(int Chunk, SimpleResolutionKind Kind, MergeResolution Resolution);

/// <summary>The text of a conflict file after its simple conflicts were resolved.</summary>
public sealed record ConflictTextResolution(string Text, int ConflictCount, IReadOnlyList<SimpleResolution> Resolutions)
{
    /// <summary>The number of conflicts resolved automatically.</summary>
    public int ResolvedCount => Resolutions.Count;

    /// <summary>The number of conflicts left with their markers.</summary>
    public int RemainingCount => ConflictCount - ResolvedCount;
}

/// <summary>
/// Resolves conflicts that need no judgement: identical changes, whitespace-only changes on one side, and
/// changes to different lines inside one conflict block.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Resolves every unresolved conflict of a session that has a safe answer and returns what was done.
    /// Conflicts that already have a choice are left alone.
    /// </summary>
    public static IReadOnlyList<SimpleResolution> ResolveSimple(MergeSession session, CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        return ResolveSimple(session, baseKnown: true, cancellationToken);
    }

    /// <summary>
    /// Resolves the simple conflicts of text with git-style conflict markers. Resolved blocks are replaced by
    /// their merged lines; the other blocks are kept exactly as written. Without base sections (diff3 style)
    /// only identical changes can be resolved.
    /// </summary>
    public static ConflictTextResolution ResolveConflictText(string text, CancellationToken cancellationToken = default)
    {
        // Parse first so malformed markers are reported the same way as everywhere else.
        var parts = ConflictFile.Parse(text);
        var document = TextDocument.Parse(text);
        var output = new StringBuilder(text.Length);
        var resolutions = new List<SimpleResolution>();
        var conflict = 0;
        var i = 0;
        while (i < document.LineCount)
        {
            if (Marker(document[i].Content) != '<')
            {
                AppendLine(output, document[i]);
                i++;
                continue;
            }

            var block = ReadBlock(document, i);
            var resolution = ResolveBlock(block, conflict, output, cancellationToken);
            if (resolution is null)
            {
                for (var line = i; line <= block.EndLine; line++)
                {
                    AppendLine(output, document[line]);
                }
            }
            else
            {
                resolutions.Add(resolution);
            }

            i = block.EndLine + 1;
            conflict++;
        }

        return new ConflictTextResolution(output.ToString(), parts.ConflictCount, resolutions);
    }

    internal static IReadOnlyList<SimpleResolution> ResolveSimple(MergeSession session, bool baseKnown, CancellationToken cancellationToken)
    {
        var resolutions = new List<SimpleResolution>();
        var chunks = session.Result.Chunks;
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunks[i].Kind != MergeChunkKind.Conflict || session.Resolution(i) != MergeResolution.Unresolved)
            {
                continue;
            }

            var resolution = TryResolve(session, i, baseKnown, cancellationToken);
            if (resolution is not null)
            {
                resolutions.Add(resolution);
            }
        }

        return resolutions;
    }

    private static SimpleResolution? TryResolve(MergeSession session, int index, bool baseKnown, CancellationToken cancellationToken)
    {
        var result = session.Result;
        var chunk = result.Chunks[index];
        var left = Slice(result.Left, chunk.LeftStart, chunk.LeftEnd);
        var right = Slice(result.Right, chunk.RightStart, chunk.RightEnd);
        if (SameLines(left, right))
        {
            return Apply(session, index, SimpleResolutionKind.SameChange, MergeResolution.Left);
        }

        if (!baseKnown)
        {
            return null;
        }

        var original = Slice(result.Base, chunk.BaseStart, chunk.BaseEnd);
        var baseText = WithoutWhitespace(original);
        var leftWhitespaceOnly = baseText == WithoutWhitespace(left);
        var rightWhitespaceOnly = baseText == WithoutWhitespace(right);
        if (leftWhitespaceOnly && !rightWhitespaceOnly)
        {
            return Apply(session, index, SimpleResolutionKind.LeftWhitespaceOnly, MergeResolution.Right);
        }

        if (rightWhitespaceOnly && !leftWhitespaceOnly)
        {
            return Apply(session, index, SimpleResolutionKind.RightWhitespaceOnly, MergeResolution.Left);
        }

        var merged = MergeDifferentLines(original, left, right, cancellationToken);
        if (merged is null)
        {
            return null;
        }

        session.SetCustomLines(index, merged);
        return new SimpleResolution(index, SimpleResolutionKind.DifferentLines, MergeResolution.Custom);
    }

    private static SimpleResolution Apply(MergeSession session, int index, SimpleResolutionKind kind, MergeResolution resolution)
    {
        session.Resolve(index, resolution);
        return new SimpleResolution(index, kind, resolution);
    }

    /// <summary>
    /// A line-level three-way merge inside one conflict block. Unlike the file-level merge, changes that only
    /// touch each other are combined; changes to overlapping base lines, or an insertion next to the other
    /// side's change, still conflict unless both sides did exactly the same.
    /// </summary>
    private static List<TextLine>? MergeDifferentLines(
        List<TextLine> original, List<TextLine> left, List<TextLine> right, CancellationToken cancellationToken)
    {
        var interned = new Dictionary<string, int>(StringComparer.Ordinal);
        var baseKeys = Keys(original, interned);
        var leftKeys = Keys(left, interned);
        var rightKeys = Keys(right, interned);
        var leftHunks = TextComparer.ComputeHunks(baseKeys, leftKeys, DiffAlgorithmKind.Histogram, cancellationToken);
        var rightHunks = TextComparer.ComputeHunks(baseKeys, rightKeys, DiffAlgorithmKind.Histogram, cancellationToken);

        var edits = new List<LineEdit>();
        foreach (var hunk in leftHunks)
        {
            edits.Add(new LineEdit(hunk, left));
        }

        foreach (var hunk in rightHunks)
        {
            var duplicate = false;
            foreach (var other in leftHunks)
            {
                if (!Overlaps(hunk, other))
                {
                    continue;
                }

                if (!SameEdit(hunk, rightKeys, other, leftKeys))
                {
                    return null;
                }

                duplicate = true;
            }

            if (!duplicate)
            {
                edits.Add(new LineEdit(hunk, right));
            }
        }

        edits.Sort((a, b) => a.Hunk.LeftStart != b.Hunk.LeftStart
            ? a.Hunk.LeftStart.CompareTo(b.Hunk.LeftStart)
            : a.Hunk.LeftCount.CompareTo(b.Hunk.LeftCount));
        return ApplyEdits(original, edits);
    }

    private static List<TextLine> ApplyEdits(List<TextLine> original, List<LineEdit> edits)
    {
        var merged = new List<TextLine>();
        var position = 0;
        foreach (var edit in edits)
        {
            merged.AddRange(original.GetRange(position, edit.Hunk.LeftStart - position));
            merged.AddRange(edit.Source.GetRange(edit.Hunk.RightStart, edit.Hunk.RightCount));
            position = edit.Hunk.LeftEnd;
        }

        merged.AddRange(original.GetRange(position, original.Count - position));
        return merged;
    }

    // Base ranges overlap, or an insertion sits inside or at either edge of the other change.
    private static bool Overlaps(DiffHunk a, DiffHunk b)
    {
        if (a.LeftCount == 0 || b.LeftCount == 0)
        {
            return a.LeftStart <= b.LeftEnd && b.LeftStart <= a.LeftEnd;
        }

        return a.LeftStart < b.LeftEnd && b.LeftStart < a.LeftEnd;
    }

    private static bool SameEdit(DiffHunk a, int[] aKeys, DiffHunk b, int[] bKeys)
    {
        if (a.LeftStart != b.LeftStart || a.LeftCount != b.LeftCount || a.RightCount != b.RightCount)
        {
            return false;
        }

        for (var k = 0; k < a.RightCount; k++)
        {
            if (aKeys[a.RightStart + k] != bKeys[b.RightStart + k])
            {
                return false;
            }
        }

        return true;
    }

    private static int[] Keys(List<TextLine> lines, Dictionary<string, int> interned)
    {
        var keys = new int[lines.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            // The terminator is part of the key so a line-ending change is not lost.
            var key = lines[i].Content + "\u0001" + lines[i].EndingText;
            if (!interned.TryGetValue(key, out var id))
            {
                id = interned.Count;
                interned.Add(key, id);
            }

            keys[i] = id;
        }

        return keys;
    }

    private static bool SameLines(List<TextLine> a, List<TextLine> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Content != b[i].Content || a[i].Ending != b[i].Ending)
            {
                return false;
            }
        }

        return true;
    }

    private static string WithoutWhitespace(List<TextLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            foreach (var c in line.Content)
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }
        }

        return sb.ToString();
    }

    private static List<TextLine> Slice(TextDocument document, int start, int end)
    {
        var lines = new List<TextLine>(end - start);
        for (var i = start; i < end; i++)
        {
            lines.Add(document[i]);
        }

        return lines;
    }

    private static SimpleResolution? ResolveBlock(ConflictBlock block, int conflict, StringBuilder output, CancellationToken cancellationToken)
    {
        var original = block.Base ?? new List<TextLine>();
        var chunk = new MergeChunk(MergeChunkKind.Conflict, 0, original.Count, 0, block.Left.Count, 0, block.Right.Count);
        var result = new MergeResult(
            new TextDocument(original), new TextDocument(block.Left), new TextDocument(block.Right), new[] { chunk });
        var session = new MergeSession(result);
        var resolutions = ResolveSimple(session, block.Base is not null, cancellationToken);
        if (resolutions.Count == 0)
        {
            return null;
        }

        output.Append(session.ToText());
        return resolutions[0] with { Chunk = conflict };
    }

    // Reads one well-formed block (ConflictFile.Parse has already validated the markers).
    private static ConflictBlock ReadBlock(TextDocument document, int start)
    {
        var block = new ConflictBlock();
        var section = block.Left;
        var i = start + 1;
        while (true)
        {
            var line = document[i];
            switch (Marker(line.Content))
            {
                case '|':
                    block.Base = new List<TextLine>();
                    section = block.Base;
                    break;
                case '=':
                    section = block.Right;
                    break;
                case '>':
                    block.EndLine = i;
                    return block;
                default:
                    section.Add(line);
                    break;
            }

            i++;
        }
    }

    private static void AppendLine(StringBuilder sb, TextLine line)
    {
        sb.Append(line.Content).Append(line.EndingText);
    }

    // Seven identical marker characters followed by the end of the line, a space or a tab.
    private static char Marker(string content)
    {
        if (content.Length < 7 || (content[0] != '<' && content[0] != '|' && content[0] != '=' && content[0] != '>'))
        {
            return '\0';
        }

        for (var i = 1; i < 7; i++)
        {
            if (content[i] != content[0])
            {
                return '\0';
            }
        }

        return content.Length == 7 || content[7] == ' ' || content[7] == '\t' ? content[0] : '\0';
    }

    private readonly struct LineEdit
    {
        public LineEdit(DiffHunk hunk, List<TextLine> source)
        {
            Hunk = hunk;
            Source = source;
        }

        public DiffHunk Hunk { get; }

        public List<TextLine> Source { get; }
    }

    private sealed class ConflictBlock
    {
        public List<TextLine> Left { get; } = new();

        public List<TextLine> Right { get; } = new();

        public List<TextLine>? Base { get; set; }

        public int EndLine { get; set; }
    }
}
