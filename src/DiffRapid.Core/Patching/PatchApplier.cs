using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Patching;

/// <summary>Controls hunk matching and direction.</summary>
public sealed record PatchApplyOptions
{
    /// <summary>Maximum leading and trailing context lines to ignore, from zero to two.</summary>
    public int Fuzz { get; init; } = 2;

    /// <summary>Collapse whitespace when matching source lines.</summary>
    public bool IgnoreWhitespace { get; init; }

    /// <summary>Swap the old and new sides before applying.</summary>
    public bool Reverse { get; init; }
}

/// <summary>The result of locating a hunk.</summary>
public enum HunkStatus
{
    /// <summary>The hunk matched its expected position exactly.</summary>
    Applied,

    /// <summary>The hunk matched at a different position.</summary>
    AppliedWithOffset,

    /// <summary>The hunk matched after ignoring edge context.</summary>
    AppliedWithFuzz,

    /// <summary>The new side was already present.</summary>
    AlreadyApplied,

    /// <summary>Neither side could be matched.</summary>
    Failed,
}

/// <summary>A hunk's status and displacement in lines.</summary>
/// <param name="Status">The matching result.</param>
/// <param name="Offset">Displacement from the expected position.</param>
public sealed record HunkOutcome(HunkStatus Status, int Offset = 0);

/// <summary>Patched text and individual hunk outcomes.</summary>
/// <param name="Text">The text after applying all matching hunks.</param>
/// <param name="Outcomes">The result for each hunk in patch order.</param>
public sealed record PatchApplyResult(string Text, IReadOnlyList<HunkOutcome> Outcomes)
{
    /// <summary>True when no hunk failed.</summary>
    public bool Succeeded => Outcomes.All(o => o.Status != HunkStatus.Failed);
}

/// <summary>Applies unified hunks while preserving existing line terminators.</summary>
public static class PatchApplier
{
    /// <summary>
    /// Applies a file patch, searching nearest positions before relaxing edge context. Hunk positions come from the
    /// patch, so the search is limited to the lines of <paramref name="original"/>: the work grows with the file and
    /// the hunks, never with the line numbers the patch claims.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The fuzz option is outside zero to two.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static PatchApplyResult Apply(string original, FilePatch patch, PatchApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PatchApplyOptions();
        if (options.Fuzz < 0 || options.Fuzz > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var document = TextDocument.Parse(original);
        var lines = new List<TextLine>(document.Lines);
        var ending = document.Lines.Where(l => l.Ending != LineEnding.None).GroupBy(l => l.Ending)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
        if (ending == LineEnding.None)
        {
            ending = LineEnding.Lf;
        }

        // With whitespace ignored, each file line is normalized once here and kept in step by EditHunk, instead of
        // once per candidate position.
        var keys = options.IgnoreWhitespace ? lines.Select(l => Normalize(l.Content)).ToList() : null;
        var file = new Source(lines, keys);
        var outcomes = new List<HunkOutcome>();
        // Long arithmetic: hunk starts are untrusted and may be near int.MaxValue, so the expected position and the
        // accumulated shift can leave the int range without any line ever matching there.
        long shift = 0;
        var boundary = 0;
        foreach (var patchHunk in patch.Hunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hunk = new Hunk(patchHunk, options);
            var count = options.Reverse ? patchHunk.NewCount : patchHunk.OldCount;
            var start = options.Reverse ? patchHunk.NewStart : patchHunk.OldStart;
            var expected = (count == 0 ? start : start - 1L) + shift;
            var delta = hunk.New.Length - hunk.Old.Length;
            if (count == 0 && hunk.Body.Any(l => l.Kind == '+') && Matches(file, hunk.New, expected, 0, 0))
            {
                outcomes.Add(new HunkOutcome(HunkStatus.AlreadyApplied));
                shift += delta;
                boundary = (int)(expected + delta);
                continue;
            }

            var location = LocateHunk(file, hunk, expected, boundary, count == 0, options, cancellationToken);
            if (location is { } match)
            {
                boundary = EditHunk(file, hunk.Body, match.Position, ending);
                shift += match.Position - expected + delta;
                var status = match.Fuzz > 0 ? HunkStatus.AppliedWithFuzz
                    : match.Position == expected ? HunkStatus.Applied : HunkStatus.AppliedWithOffset;
                outcomes.Add(new HunkOutcome(status, ClampOffset(match.Position - expected)));
                continue;
            }

            var alreadyApplied = LocateAlreadyApplied(file, hunk, boundary, cancellationToken);
            if (alreadyApplied is { } position)
            {
                shift += position - expected + delta;
                boundary = position + hunk.New.Length;
            }

            outcomes.Add(new HunkOutcome(alreadyApplied.HasValue ? HunkStatus.AlreadyApplied : HunkStatus.Failed));
        }

        return new PatchApplyResult(new TextDocument(lines).ToString(), outcomes.AsReadOnly());
    }

    private static int ClampOffset(long offset) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, offset));

    private static (int Position, int Fuzz)? LocateHunk(Source file, Hunk hunk,
        long expected, int boundary, bool insertion, PatchApplyOptions options, CancellationToken cancellationToken)
    {
        for (var fuzz = 0; fuzz <= options.Fuzz; fuzz++)
        {
            var leading = Math.Min(fuzz, hunk.LeadingContext);
            var trailing = Math.Min(fuzz, hunk.TrailingContext);
            if (expected >= boundary && Matches(file, hunk.Old, expected, leading, trailing))
            {
                return ((int)expected, fuzz);
            }

            if (insertion)
            {
                continue;
            }

            var position = LocateOffset(file, hunk.Old, expected, boundary, leading, trailing, cancellationToken);
            if (position.HasValue)
            {
                return (position.Value, fuzz);
            }
        }

        return null;
    }

    /// <summary>
    /// Tries every position in [<paramref name="boundary"/>, lines.Count] other than <paramref name="expected"/>,
    /// nearest first (the lower one on a tie), so at most lines.Count + 1 candidates are tested wherever the
    /// patch says the hunk starts.
    /// </summary>
    private static int? LocateOffset(Source file, Side side, long expected,
        int boundary, int leading, int trailing, CancellationToken cancellationToken)
    {
        var lineCount = file.Lines.Count;
        var low = Math.Min(expected - 1, lineCount);
        var high = Math.Max(expected + 1, boundary);
        while (low >= boundary || high <= lineCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var takeLow = low >= boundary && (high > lineCount || expected - low <= high - expected);
            var position = (int)(takeLow ? low-- : high++);
            if (Matches(file, side, position, leading, trailing))
            {
                return position;
            }
        }

        return null;
    }

    private static int? LocateAlreadyApplied(Source file, Hunk hunk, int boundary, CancellationToken cancellationToken)
    {
        for (var position = boundary; position <= file.Lines.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(file, hunk.New, position, 0, 0))
            {
                return position;
            }
        }

        return null;
    }

    private static int EditHunk(Source file, PatchLine[] body, int position, LineEnding ending)
    {
        var cursor = position;
        foreach (var line in body)
        {
            if (line.Kind == '-')
            {
                file.Lines.RemoveAt(cursor);
                file.Keys?.RemoveAt(cursor);
            }
            else if (line.Kind == '+')
            {
                file.Lines.Insert(cursor, new TextLine(line.Text, line.NoNewline ? LineEnding.None : ending));
                file.Keys?.Insert(cursor, Normalize(line.Text));
                cursor++;
            }
            else
            {
                cursor++;
            }
        }

        return cursor;
    }

    private static bool Matches(Source file, Side side, long position, int leading, int trailing)
    {
        var lines = file.Lines;
        if (position < 0 || position > lines.Count - side.Length)
        {
            return false;
        }

        for (var index = leading; index < side.Length - trailing; index++)
        {
            var at = (int)position + index;
            var actual = file.Keys is null ? lines[at].Content : file.Keys[at];
            if (actual != side.Keys[index] || (lines[at].Ending == LineEnding.None) != side.NoNewline[index])
            {
                return false;
            }
        }

        return true;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Normalize(string text) => Whitespace.Replace(text, " ").Trim();

    /// <summary>The file's lines and, when whitespace is ignored, their normalized form at the same indexes.</summary>
    private sealed class Source
    {
        public Source(List<TextLine> lines, List<string>? keys) => (Lines, Keys) = (lines, keys);

        public List<TextLine> Lines { get; }

        public List<string>? Keys { get; }
    }

    /// <summary>One side of a hunk (context plus deletions, or context plus insertions), ready to compare.</summary>
    private sealed class Side
    {
        public Side(PatchLine[] body, char excluded, bool ignoreWhitespace)
        {
            var lines = body.Where(l => l.Kind != excluded).ToArray();
            Keys = lines.Select(l => ignoreWhitespace ? Normalize(l.Text) : l.Text).ToArray();
            NoNewline = lines.Select(l => l.NoNewline).ToArray();
        }

        public string[] Keys { get; }

        public bool[] NoNewline { get; }

        public int Length => Keys.Length;
    }

    /// <summary>A hunk in the applied direction with its two sides and edge context computed once.</summary>
    private sealed class Hunk
    {
        public Hunk(PatchHunk hunk, PatchApplyOptions options)
        {
            Body = hunk.Lines.Select(l => options.Reverse
                ? l with { Kind = l.Kind == '+' ? '-' : l.Kind == '-' ? '+' : ' ' } : l).ToArray();
            Old = new Side(Body, '+', options.IgnoreWhitespace);
            New = new Side(Body, '-', options.IgnoreWhitespace);
            LeadingContext = Body.TakeWhile(l => l.Kind == ' ').Count();
            TrailingContext = Enumerable.Reverse(Body).TakeWhile(l => l.Kind == ' ').Count();
        }

        public PatchLine[] Body { get; }

        public Side Old { get; }

        public Side New { get; }

        public int LeadingContext { get; }

        public int TrailingContext { get; }
    }
}
