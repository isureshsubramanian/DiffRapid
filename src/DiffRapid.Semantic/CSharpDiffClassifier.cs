using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Semantic.Internal;
using Microsoft.CodeAnalysis.CSharp;

namespace DiffRapid.Semantic;

/// <summary>Why a C# difference does not matter.</summary>
public enum CSharpUnimportantReason
{
    /// <summary>Only whitespace, indentation, blank lines or line breaks differ.</summary>
    Formatting,

    /// <summary>Only comments differ (and possibly formatting).</summary>
    Comments,

    /// <summary>Only #region or #endregion lines differ (and possibly comments or formatting).</summary>
    Regions,

    /// <summary>Only the order of using directives differs; both files have the same usings.</summary>
    UsingOrder,
}

/// <summary>A hunk of a text comparison that is unimportant in C# terms.</summary>
public sealed record UnimportantHunk(int HunkIndex, DiffHunk Hunk, CSharpUnimportantReason Reason)
{
    /// <summary>A short explanation for display.</summary>
    public string Description => Reason switch
    {
        CSharpUnimportantReason.Formatting => "only whitespace or line breaks differ",
        CSharpUnimportantReason.Comments => "only comments differ",
        CSharpUnimportantReason.Regions => "only #region lines differ",
        _ => "only the order of using directives differs",
    };
}

/// <summary>Finds the hunks of a line comparison of two C# files that only differ in trivia, comments, regions or using order.</summary>
public static class CSharpDiffClassifier
{
    /// <summary>Classifies the hunks of a comparison of two C# documents; hunks that matter are not listed.</summary>
    public static IReadOnlyList<UnimportantHunk> Classify(TextDiffResult result, CancellationToken cancellationToken = default)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var left = Side.Create(result.Left.ToString(), cancellationToken);
        var right = Side.Create(result.Right.ToString(), cancellationToken);
        var unimportant = new List<UnimportantHunk>();
        for (var i = 0; i < result.Hunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hunk = result.Hunks[i];
            var reason = ReasonFor(left, right, hunk);
            if (reason is { } found)
            {
                unimportant.Add(new UnimportantHunk(i, hunk, found));
            }
        }

        return unimportant;
    }

    private static CSharpUnimportantReason? ReasonFor(Side left, Side right, DiffHunk hunk)
    {
        if (SameContent(left.Formatting, right.Formatting, hunk))
        {
            return CSharpUnimportantReason.Formatting;
        }

        if (SameContent(left.Comments, right.Comments, hunk))
        {
            return CSharpUnimportantReason.Comments;
        }

        if (SameContent(left.Regions, right.Regions, hunk))
        {
            return CSharpUnimportantReason.Regions;
        }

        if (OnlyUsings(left, right, hunk))
        {
            return CSharpUnimportantReason.UsingOrder;
        }

        return null;
    }

    /// <summary>The hunk's lines on both sides read the same once normalized and joined.</summary>
    private static bool SameContent(NormalizedText left, NormalizedText right, DiffHunk hunk)
    {
        return string.Equals(Joined(left, hunk.LeftStart, hunk.LeftCount), Joined(right, hunk.RightStart, hunk.RightCount), StringComparison.Ordinal);
    }

    private static string Joined(NormalizedText text, int start, int count)
    {
        var parts = new List<string>(count);
        for (var line = start; line < start + count && line < text.OriginalLineCount; line++)
        {
            var content = text.ForOriginalLine(line);
            if (!string.IsNullOrEmpty(content))
            {
                parts.Add(content!);
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>The hunk holds only using directives and blank lines, and both files have the same usings.</summary>
    private static bool OnlyUsings(Side left, Side right, DiffHunk hunk)
    {
        var leftUsings = left.CountUsingLines(hunk.LeftStart, hunk.LeftCount);
        var rightUsings = right.CountUsingLines(hunk.RightStart, hunk.RightCount);
        if (leftUsings < 0 || rightUsings < 0 || leftUsings + rightUsings == 0)
        {
            return false;
        }

        return left.Usings.SequenceEqual(right.Usings, StringComparer.Ordinal);
    }

    /// <summary>One file normalized at each level of ignoring.</summary>
    private sealed class Side
    {
        private readonly HashSet<int> _usingLines;

        private Side(NormalizedText formatting, NormalizedText comments, NormalizedText regions, HashSet<int> usingLines, List<string> usings)
        {
            Formatting = formatting;
            Comments = comments;
            Regions = regions;
            _usingLines = usingLines;
            Usings = usings;
        }

        public NormalizedText Formatting { get; }

        public NormalizedText Comments { get; }

        public NormalizedText Regions { get; }

        /// <summary>All using directives of the file, sorted.</summary>
        public List<string> Usings { get; }

        public static Side Create(string text, CancellationToken cancellationToken)
        {
            var tree = CSharpSyntaxTree.ParseText(text, CSharpNormalizer.ParseOptions, cancellationToken: cancellationToken);
            var table = LineTable.Create(text);
            var formatting = CSharpIgnoreOptions.Formatting;
            var comments = formatting | CSharpIgnoreOptions.Comments;
            var regions = comments | CSharpIgnoreOptions.Regions;
            var usingLines = new HashSet<int>();
            var usings = new List<string>();
            foreach (var directive in CSharpNormalizer.UsingGroups(tree.GetRoot(cancellationToken)).SelectMany(group => group))
            {
                usings.Add(MemberCollector.Compact(directive));
                var first = table.LineOf(directive.Span.Start);
                var last = table.LineOf(Math.Max(directive.Span.Start, directive.Span.End - 1));
                for (var line = first; line <= last; line++)
                {
                    usingLines.Add(line);
                }
            }

            usings.Sort(StringComparer.Ordinal);
            return new Side(
                CSharpNormalizer.Normalize(tree, text, table, formatting, cancellationToken),
                CSharpNormalizer.Normalize(tree, text, table, comments, cancellationToken),
                CSharpNormalizer.Normalize(tree, text, table, regions, cancellationToken),
                usingLines,
                usings);
        }

        /// <summary>The number of using lines in a range, or -1 when the range holds anything but usings and blank lines.</summary>
        public int CountUsingLines(int start, int count)
        {
            var found = 0;
            for (var line = start; line < start + count && line < Formatting.OriginalLineCount; line++)
            {
                if (_usingLines.Contains(line))
                {
                    found++;
                }
                else if (!string.IsNullOrEmpty(Formatting.ForOriginalLine(line)))
                {
                    return -1;
                }
            }

            return found;
        }
    }
}
