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

        var outcomes = new List<HunkOutcome>();
        // Long arithmetic: hunk starts are untrusted and may be near int.MaxValue, so the expected position and the
        // accumulated shift can leave the int range without any line ever matching there.
        long shift = 0;
        var boundary = 0;
        foreach (var hunk in patch.Hunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = hunk.Lines.Select(l => options.Reverse
                ? l with { Kind = l.Kind == '+' ? '-' : l.Kind == '-' ? '+' : ' ' } : l).ToArray();
            var count = options.Reverse ? hunk.NewCount : hunk.OldCount;
            var start = options.Reverse ? hunk.NewStart : hunk.OldStart;
            var expected = (count == 0 ? start : start - 1L) + shift;
            var delta = body.Count(l => l.Kind == '+') - body.Count(l => l.Kind == '-');
            if (count == 0 && body.Any(l => l.Kind == '+') && Matches(lines, body, expected, 0, 0, true, options))
            {
                outcomes.Add(new HunkOutcome(HunkStatus.AlreadyApplied));
                shift += delta;
                boundary = (int)(expected + delta);
                continue;
            }

            var location = LocateHunk(lines, body, expected, boundary, count == 0, options, cancellationToken);
            if (location is { } match)
            {
                boundary = EditHunk(lines, body, match.Position, ending);
                shift += match.Position - expected + delta;
                var status = match.Fuzz > 0 ? HunkStatus.AppliedWithFuzz
                    : match.Position == expected ? HunkStatus.Applied : HunkStatus.AppliedWithOffset;
                outcomes.Add(new HunkOutcome(status, ClampOffset(match.Position - expected)));
                continue;
            }

            var alreadyApplied = LocateAlreadyApplied(lines, body, boundary, options);
            if (alreadyApplied is { } position)
            {
                shift += position - expected + delta;
                boundary = position + body.Count(l => l.Kind != '-');
            }

            outcomes.Add(new HunkOutcome(alreadyApplied.HasValue ? HunkStatus.AlreadyApplied : HunkStatus.Failed));
        }

        return new PatchApplyResult(new TextDocument(lines).ToString(), outcomes.AsReadOnly());
    }

    private static int ClampOffset(long offset) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, offset));

    private static (int Position, int Fuzz)? LocateHunk(List<TextLine> lines, PatchLine[] body,
        long expected, int boundary, bool insertion, PatchApplyOptions options, CancellationToken cancellationToken)
    {
        for (var fuzz = 0; fuzz <= options.Fuzz; fuzz++)
        {
            var leading = Math.Min(fuzz, body.TakeWhile(l => l.Kind == ' ').Count());
            var trailing = Math.Min(fuzz, Enumerable.Reverse(body).TakeWhile(l => l.Kind == ' ').Count());
            if (expected >= boundary && Matches(lines, body, expected, leading, trailing, false, options))
            {
                return ((int)expected, fuzz);
            }

            if (insertion)
            {
                continue;
            }

            var position = LocateOffset(lines, body, expected, boundary, leading, trailing, options, cancellationToken);
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
    private static int? LocateOffset(List<TextLine> lines, PatchLine[] body, long expected,
        int boundary, int leading, int trailing, PatchApplyOptions options, CancellationToken cancellationToken)
    {
        var low = Math.Min(expected - 1, lines.Count);
        var high = Math.Max(expected + 1, boundary);
        while (low >= boundary || high <= lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var takeLow = low >= boundary && (high > lines.Count || expected - low <= high - expected);
            var position = (int)(takeLow ? low-- : high++);
            if (Matches(lines, body, position, leading, trailing, false, options))
            {
                return position;
            }
        }

        return null;
    }

    private static int? LocateAlreadyApplied(List<TextLine> lines, PatchLine[] body, int boundary, PatchApplyOptions options)
    {
        for (var position = boundary; position <= lines.Count; position++)
        {
            if (Matches(lines, body, position, 0, 0, true, options))
            {
                return position;
            }
        }

        return null;
    }

    private static int EditHunk(List<TextLine> lines, PatchLine[] body, int position, LineEnding ending)
    {
        var cursor = position;
        foreach (var line in body)
        {
            if (line.Kind == '-')
            {
                lines.RemoveAt(cursor);
            }
            else if (line.Kind == '+')
            {
                lines.Insert(cursor, new TextLine(line.Text, line.NoNewline ? LineEnding.None : ending));
                cursor++;
            }
            else
            {
                cursor++;
            }
        }

        return cursor;
    }

    private static bool Matches(List<TextLine> lines, PatchLine[] body, long position,
        int leading, int trailing, bool newSide, PatchApplyOptions options)
    {
        var source = body.Where(l => l.Kind != (newSide ? '-' : '+')).ToArray();
        if (position < 0 || position > lines.Count - source.Length)
        {
            return false;
        }

        for (var index = 0; index < source.Length; index++)
        {
            if (index < leading || index >= source.Length - trailing)
            {
                continue;
            }

            var actual = lines[(int)position + index];
            var wanted = source[index];
            if (Normalize(actual.Content, options) != Normalize(wanted.Text, options)
                || (actual.Ending == LineEnding.None) != wanted.NoNewline)
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string text, PatchApplyOptions options)
        => options.IgnoreWhitespace ? Regex.Replace(text, @"\s+", " ").Trim() : text;
}
