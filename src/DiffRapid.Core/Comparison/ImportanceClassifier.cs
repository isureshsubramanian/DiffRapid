using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>A changed range inside a line and the rules, if any, that make it unimportant.</summary>
public readonly record struct SegmentImportance(TextSpan Span, UnimportantReasons Reasons)
{
    /// <summary>True when no rule applies to the segment.</summary>
    public bool IsImportant => Reasons == UnimportantReasons.None;
}

/// <summary>The inline segments of one pair of changed lines.</summary>
public sealed record LineImportance(
    int LeftLine, int RightLine, IReadOnlyList<SegmentImportance> Left, IReadOnlyList<SegmentImportance> Right);

/// <summary>
/// How much one hunk matters. <see cref="Lines"/> pairs the hunk's changed lines by position and is
/// classified independently of the hunk as a whole.
/// </summary>
public sealed record HunkImportance(
    int HunkIndex, DiffHunk Hunk, UnimportantReasons Reasons, string? Reason, IReadOnlyList<LineImportance> Lines)
{
    /// <summary>Important, or Unimportant when at least one rule applies.</summary>
    public Importance Importance => Reasons == UnimportantReasons.None ? Importance.Important : Importance.Unimportant;

    /// <summary>True when the hunk is a real difference.</summary>
    public bool IsImportant => Reasons == UnimportantReasons.None;
}

/// <summary>The importance of every hunk of a comparison, with navigation between important hunks.</summary>
public sealed class ImportanceResult
{
    internal ImportanceResult(IReadOnlyList<HunkImportance> hunks)
    {
        Hunks = hunks;
        ImportantCount = hunks.Count(h => h.IsImportant);
    }

    /// <summary>One entry per hunk of the comparison, in the same order.</summary>
    public IReadOnlyList<HunkImportance> Hunks { get; }

    /// <summary>The number of important hunks.</summary>
    public int ImportantCount { get; }

    /// <summary>The number of unimportant hunks.</summary>
    public int UnimportantCount => Hunks.Count - ImportantCount;

    /// <summary>Finds the next important hunk strictly after fromHunk; -1 starts at the beginning. Returns -1 when none.</summary>
    public int NextImportant(int fromHunk)
    {
        ValidatePosition(fromHunk);
        for (var i = fromHunk + 1; i < Hunks.Count; i++)
        {
            if (Hunks[i].IsImportant)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Finds the previous important hunk strictly before fromHunk; the hunk count starts at the end. Returns -1 when none.</summary>
    public int PreviousImportant(int fromHunk)
    {
        ValidatePosition(fromHunk);
        for (var i = fromHunk - 1; i >= 0; i--)
        {
            if (Hunks[i].IsImportant)
            {
                return i;
            }
        }

        return -1;
    }

    private void ValidatePosition(int fromHunk)
    {
        if (fromHunk < -1 || fromHunk > Hunks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fromHunk));
        }
    }
}

/// <summary>Applies <see cref="ImportanceRules"/> to the hunks and inline segments of a comparison.</summary>
public static class ImportanceClassifier
{
    private static readonly UnimportantReasons[] SingleRules =
    {
        UnimportantReasons.BlankLines,
        UnimportantReasons.Whitespace,
        UnimportantReasons.Case,
        UnimportantReasons.Comment,
        UnimportantReasons.Pattern,
    };

    /// <summary>Classifies a comparison using the documents it was made from.</summary>
    public static ImportanceResult Classify(TextDiffResult result, ImportanceRules rules, CancellationToken cancellationToken = default)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return Classify(result, result.Left, result.Right, rules, cancellationToken);
    }

    /// <summary>
    /// Classifies each hunk as Important or Unimportant with the rules that apply, and flags the changed
    /// segments of each pair of changed lines. The documents must be the ones the hunks refer to.
    /// </summary>
    public static ImportanceResult Classify(
        TextDiffResult result, TextDocument left, TextDocument right, ImportanceRules rules, CancellationToken cancellationToken = default)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (rules is null)
        {
            throw new ArgumentNullException(nameof(rules));
        }

        var context = new Classification(result, left, right, rules, cancellationToken);
        var hunks = new HunkImportance[result.Hunks.Count];
        for (var i = 0; i < hunks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hunks[i] = context.ClassifyHunk(i);
        }

        return new ImportanceResult(hunks);
    }

    /// <summary>Describes a set of reasons in words, for tooltips and reports.</summary>
    public static string Describe(UnimportantReasons reasons)
    {
        var parts = new List<string>();
        AddPart(parts, reasons, UnimportantReasons.Filtered, "ignored by compare options");
        AddPart(parts, reasons, UnimportantReasons.BlankLines, "blank lines");
        AddPart(parts, reasons, UnimportantReasons.Whitespace, "whitespace");
        AddPart(parts, reasons, UnimportantReasons.Case, "case");
        AddPart(parts, reasons, UnimportantReasons.Comment, "comments");
        AddPart(parts, reasons, UnimportantReasons.Pattern, "unimportant text");
        AddPart(parts, reasons, UnimportantReasons.Custom, "custom rule");
        return string.Join(", ", parts);
    }

    private static void AddPart(List<string> parts, UnimportantReasons reasons, UnimportantReasons flag, string text)
    {
        if ((reasons & flag) != 0)
        {
            parts.Add(text);
        }
    }

    private sealed class Classification
    {
        private readonly TextDiffResult _result;
        private readonly TextDocument _left;
        private readonly TextDocument _right;
        private readonly ImportanceRules _rules;
        private readonly Regex[] _patterns;
        private readonly TextSpan[][]? _leftComments;
        private readonly TextSpan[][]? _rightComments;
        private readonly UnimportantReasons _enabled;
        private readonly CompareOptions _strictInline;
        private readonly CompareOptions _relaxedInline;
        private readonly LineNormalizer _normalizer;

        public Classification(TextDiffResult result, TextDocument left, TextDocument right, ImportanceRules rules, CancellationToken cancellationToken)
        {
            _result = result;
            _left = left;
            _right = right;
            _rules = rules;
            _patterns = rules.UnimportantPatterns.Select(pattern => FilterRegex.Create(pattern)).ToArray();
            if (rules.Comments is not null)
            {
                _leftComments = CommentScanner.Scan(left, rules.Comments, cancellationToken);
                _rightComments = CommentScanner.Scan(right, rules.Comments, cancellationToken);
            }

            _enabled = EnabledRules(rules, _patterns.Length > 0);
            _normalizer = new LineNormalizer(result.Options);
            _strictInline = new CompareOptions { InlineGranularity = result.Options.InlineGranularity };
            _relaxedInline = _strictInline with
            {
                Whitespace = rules.WhitespaceIsUnimportant ? WhitespaceMode.IgnoreAll : WhitespaceMode.Compare,
                IgnoreCase = rules.CaseIsUnimportant,
            };
        }

        public HunkImportance ClassifyHunk(int index)
        {
            var hunk = _result.Hunks[index];
            var lines = ClassifyLines(hunk);
            if (hunk.Significance == DiffSignificance.Ignored)
            {
                return Build(index, hunk, UnimportantReasons.Filtered, null, lines);
            }

            foreach (var rule in SingleRules)
            {
                if ((_enabled & rule) != 0 && SidesMatch(hunk, rule))
                {
                    return Build(index, hunk, rule, null, lines);
                }
            }

            if (SidesMatch(hunk, _enabled))
            {
                return Build(index, hunk, Minimize(hunk, _enabled), null, lines);
            }

            foreach (var rule in _rules.CustomRules)
            {
                var reason = rule.ExplainUnimportant(new ImportanceContext(_result, _left, _right, index, hunk));
                if (reason is not null)
                {
                    return Build(index, hunk, UnimportantReasons.Custom, reason, lines);
                }
            }

            return new HunkImportance(index, hunk, UnimportantReasons.None, null, lines);
        }

        private static HunkImportance Build(
            int index, DiffHunk hunk, UnimportantReasons reasons, string? reason, IReadOnlyList<LineImportance> lines)
        {
            return new HunkImportance(index, hunk, reasons, reason ?? Describe(reasons), lines);
        }

        private static UnimportantReasons EnabledRules(ImportanceRules rules, bool hasPatterns)
        {
            var enabled = UnimportantReasons.None;
            enabled |= rules.BlankLinesAreUnimportant ? UnimportantReasons.BlankLines : 0;
            enabled |= rules.WhitespaceIsUnimportant ? UnimportantReasons.Whitespace : 0;
            enabled |= rules.CaseIsUnimportant ? UnimportantReasons.Case : 0;
            enabled |= rules.Comments is not null ? UnimportantReasons.Comment : 0;
            enabled |= hasPatterns ? UnimportantReasons.Pattern : 0;
            return enabled;
        }

        // Drops rules from a matching combination while the sides still match, so the reasons stay minimal.
        private UnimportantReasons Minimize(DiffHunk hunk, UnimportantReasons reasons)
        {
            foreach (var rule in SingleRules)
            {
                var without = reasons & ~rule;
                if ((reasons & rule) != 0 && without != UnimportantReasons.None && SidesMatch(hunk, without))
                {
                    reasons = without;
                }
            }

            return reasons;
        }

        private bool SidesMatch(DiffHunk hunk, UnimportantReasons rules)
        {
            if (rules == UnimportantReasons.None)
            {
                return false;
            }

            var left = Canonical(_left, _leftComments, hunk.LeftStart, hunk.LeftEnd, rules);
            var right = Canonical(_right, _rightComments, hunk.RightStart, hunk.RightEnd, rules);
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        // The hunk's lines with everything the rules call unimportant removed.
        private List<string> Canonical(TextDocument document, TextSpan[][]? comments, int start, int end, UnimportantReasons rules)
        {
            var lines = new List<string>(end - start);
            for (var i = start; i < end; i++)
            {
                var original = document[i].Content;
                var content = original;

                // Comments first: their ranges were found on the original line.
                if ((rules & UnimportantReasons.Comment) != 0 && comments is not null)
                {
                    content = RemoveSpans(content, comments[i]).TrimEnd();
                }

                if ((rules & UnimportantReasons.Pattern) != 0)
                {
                    content = RemovePatterns(content);
                }

                // What the comparison itself already ignores (case, whitespace, substitutions) stays ignored.
                content = _normalizer.NormalizeContent(content);
                if ((rules & UnimportantReasons.Case) != 0)
                {
                    content = content.ToUpperInvariant();
                }

                if ((rules & UnimportantReasons.Whitespace) != 0)
                {
                    content = RemoveWhitespace(content);
                }

                // A line emptied by a rule disappears; an originally blank line only under the blank-line rule.
                var blank = LineNormalizer.IsBlank(content);
                if (blank && ((rules & UnimportantReasons.BlankLines) != 0 || !LineNormalizer.IsBlank(original)))
                {
                    continue;
                }

                var compareEndings = (rules & UnimportantReasons.Whitespace) == 0 && !_result.Options.IgnoreLineEndings;
                lines.Add(compareEndings ? content + "\u0001" + document[i].EndingText : content);
            }

            if ((rules & UnimportantReasons.Whitespace) != 0)
            {
                // Line breaks are whitespace too: a rewrapped paragraph compares equal.
                return new List<string> { string.Concat(lines) };
            }

            return lines;
        }

        private string RemovePatterns(string content)
        {
            foreach (var pattern in _patterns)
            {
                content = pattern.Replace(content, string.Empty);
            }

            return content;
        }

        private static string RemoveSpans(string content, TextSpan[] spans)
        {
            if (spans.Length == 0)
            {
                return content;
            }

            var sb = new StringBuilder(content.Length);
            var position = 0;
            foreach (var span in spans)
            {
                sb.Append(content, position, span.Start - position);
                position = span.End;
            }

            sb.Append(content, position, content.Length - position);
            return sb.ToString();
        }

        private static string RemoveWhitespace(string content)
        {
            var sb = new StringBuilder(content.Length);
            foreach (var c in content)
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private List<LineImportance> ClassifyLines(DiffHunk hunk)
        {
            var pairs = Math.Min(hunk.LeftCount, hunk.RightCount);
            var lines = new List<LineImportance>(pairs);
            for (var k = 0; k < pairs; k++)
            {
                lines.Add(ClassifyLine(hunk.LeftStart + k, hunk.RightStart + k));
            }

            return lines;
        }

        private LineImportance ClassifyLine(int leftLine, int rightLine)
        {
            var leftText = _left[leftLine].Content;
            var rightText = _right[rightLine].Content;
            var strict = InlineDiffer.Compare(leftText, rightText, _strictInline);
            var relaxed = _rules.WhitespaceIsUnimportant || _rules.CaseIsUnimportant
                ? InlineDiffer.Compare(leftText, rightText, _relaxedInline)
                : strict;
            return new LineImportance(
                leftLine,
                rightLine,
                ClassifySegments(leftText, strict.Left, relaxed.Left, _leftComments?[leftLine]),
                ClassifySegments(rightText, strict.Right, relaxed.Right, _rightComments?[rightLine]));
        }

        private List<SegmentImportance> ClassifySegments(
            string text, IReadOnlyList<TextSpan> strict, IReadOnlyList<TextSpan> relaxed, TextSpan[]? comments)
        {
            var patternMatches = PatternMatches(text);
            var segments = new List<SegmentImportance>(strict.Count);
            foreach (var span in strict)
            {
                var reasons = UnimportantReasons.None;
                if (relaxed != strict && !OverlapsAny(span, relaxed))
                {
                    reasons |= IsWhitespace(text, span) ? UnimportantReasons.Whitespace : RelaxedReasons(text, span);
                }

                if (comments is not null && Covered(span, comments))
                {
                    reasons |= UnimportantReasons.Comment;
                }

                if (patternMatches.Count > 0 && Covered(span, patternMatches))
                {
                    reasons |= UnimportantReasons.Pattern;
                }

                segments.Add(new SegmentImportance(span, reasons));
            }

            return segments;
        }

        private UnimportantReasons RelaxedReasons(string text, TextSpan span)
        {
            var reasons = _rules.CaseIsUnimportant ? UnimportantReasons.Case : UnimportantReasons.None;
            for (var i = span.Start; i < span.End; i++)
            {
                if (char.IsWhiteSpace(text[i]) && _rules.WhitespaceIsUnimportant)
                {
                    return reasons | UnimportantReasons.Whitespace;
                }
            }

            return reasons;
        }

        private List<TextSpan> PatternMatches(string text)
        {
            var matches = new List<TextSpan>();
            foreach (var pattern in _patterns)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (match.Length > 0)
                    {
                        matches.Add(new TextSpan(match.Index, match.Length));
                    }
                }
            }

            return matches;
        }

        private static bool IsWhitespace(string text, TextSpan span)
        {
            for (var i = span.Start; i < span.End; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool OverlapsAny(TextSpan span, IReadOnlyList<TextSpan> spans)
        {
            foreach (var other in spans)
            {
                if (span.Start < other.End && other.Start < span.End)
                {
                    return true;
                }
            }

            return false;
        }

        // True when every character of span lies inside one of the ranges.
        private static bool Covered(TextSpan span, IReadOnlyList<TextSpan> ranges)
        {
            var position = span.Start;
            var advanced = true;
            while (position < span.End && advanced)
            {
                advanced = false;
                foreach (var range in ranges)
                {
                    if (range.Start <= position && position < range.End)
                    {
                        position = range.End;
                        advanced = true;
                    }
                }
            }

            return position >= span.End;
        }
    }
}
