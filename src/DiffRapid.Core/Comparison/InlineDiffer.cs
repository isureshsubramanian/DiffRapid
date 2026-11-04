using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Algorithms;

namespace DiffRapid.Core.Comparison;

/// <summary>A character range inside a line: zero-based start, length.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Changed character ranges on each side of a pair of lines.</summary>
public sealed record InlineDiff(IReadOnlyList<TextSpan> Left, IReadOnlyList<TextSpan> Right);

/// <summary>Highlights the words or characters that differ between two versions of a line.</summary>
public static class InlineDiffer
{
    internal const int MaxInputLength = 10000;

    public static InlineDiff Compare(string left, string right, CompareOptions? options = null)
    {
        return Compare(left, right, options, CancellationToken.None);
    }

    /// <summary>
    /// Highlights a line pair with cancellation. Inputs over 10,000 characters use whole-line spans
    /// to bound tokenization and search cost.
    /// </summary>
    public static InlineDiff Compare(string left, string right, CompareOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return new InlineDiff(Array.Empty<TextSpan>(), Array.Empty<TextSpan>());
        }

        if (left.Length > MaxInputLength || right.Length > MaxInputLength)
        {
            return new InlineDiff(WholeLine(left), WholeLine(right));
        }

        options ??= CompareOptions.Default;
        var leftTokens = Tokenize(left, options.InlineGranularity);
        var rightTokens = Tokenize(right, options.InlineGranularity);

        var normalizer = new LineNormalizer(options with { IgnoreNumbers = false });
        var interned = new Dictionary<string, int>(System.StringComparer.Ordinal);
        var ignoreWhitespace = options.Whitespace != WhitespaceMode.Compare;

        var leftFiltered = Filter(leftTokens, left, ignoreWhitespace);
        var rightFiltered = Filter(rightTokens, right, ignoreWhitespace);
        var a = Keys(leftFiltered, left, normalizer, interned);
        var b = Keys(rightFiltered, right, normalizer, interned);

        var changedA = new bool[a.Length];
        var changedB = new bool[b.Length];
        new MyersDiff().Compare(a, b, changedA, changedB, cancellationToken);

        return new InlineDiff(ToSpans(leftFiltered, changedA), ToSpans(rightFiltered, changedB));
    }

    private static IReadOnlyList<TextSpan> WholeLine(string text)
    {
        return text.Length == 0 ? Array.Empty<TextSpan>() : new[] { new TextSpan(0, text.Length) };
    }

    internal static List<TextSpan> Tokenize(string text, InlineGranularity granularity)
    {
        var tokens = new List<TextSpan>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            if (granularity == InlineGranularity.Character)
            {
                i += char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
            }
            else
            {
                i++;
            }

            tokens.Add(new TextSpan(start, i - start));
        }

        return tokens;
    }

    private static List<TextSpan> Filter(List<TextSpan> tokens, string text, bool ignoreWhitespace)
    {
        if (!ignoreWhitespace)
        {
            return tokens;
        }

        var result = new List<TextSpan>(tokens.Count);
        foreach (var t in tokens)
        {
            if (!char.IsWhiteSpace(text[t.Start]))
            {
                result.Add(t);
            }
        }

        return result;
    }

    private static int[] Keys(List<TextSpan> tokens, string text, LineNormalizer normalizer, Dictionary<string, int> interned)
    {
        var keys = new int[tokens.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            var token = normalizer.NormalizeContent(text.Substring(tokens[i].Start, tokens[i].Length));
            if (!interned.TryGetValue(token, out var id))
            {
                id = interned.Count;
                interned.Add(token, id);
            }

            keys[i] = id;
        }

        return keys;
    }

    // Merges adjacent changed tokens into contiguous spans.
    private static List<TextSpan> ToSpans(List<TextSpan> tokens, bool[] changed)
    {
        var spans = new List<TextSpan>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (!changed[i])
            {
                i++;
                continue;
            }

            var start = tokens[i].Start;
            var end = tokens[i].End;
            i++;
            while (i < tokens.Count && changed[i])
            {
                end = tokens[i].End;
                i++;
            }

            spans.Add(new TextSpan(start, end - start));
        }

        return spans;
    }
}
