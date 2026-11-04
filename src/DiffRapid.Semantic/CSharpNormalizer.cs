using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Semantic.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiffRapid.Semantic;

/// <summary>
/// Rewrites C# source so that ignorable differences disappear, keeping one output line per original line
/// where possible. Lines that only held ignored content are dropped; with <see cref="CSharpIgnoreOptions.Formatting"/>
/// every line keeps its tokens separated by single spaces and blank lines are dropped.
/// </summary>
public static class CSharpNormalizer
{
    internal static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular);

    /// <summary>Normalizes C# source text. Syntax errors are tolerated; the text is still tokenized.</summary>
    public static NormalizedText Normalize(string text, CSharpIgnoreOptions options, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, cancellationToken: cancellationToken);
        return Normalize(tree, text, LineTable.Create(text), options, cancellationToken);
    }

    internal static NormalizedText Normalize(
        SyntaxTree tree,
        string text,
        LineTable table,
        CSharpIgnoreOptions options,
        CancellationToken cancellationToken)
    {
        var writer = new LineWriter(text, table, (options & CSharpIgnoreOptions.Formatting) != 0);
        var root = tree.GetRoot(cancellationToken);
        var count = 0;
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (++count % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            WriteTrivia(writer, token.LeadingTrivia, options);
            writer.AppendToken(token.Span.Start, token.Span.End);
            WriteTrivia(writer, token.TrailingTrivia, options);
        }

        var order = Enumerable.Range(0, table.Count).ToArray();
        if ((options & CSharpIgnoreOptions.UsingOrder) != 0)
        {
            SortUsings(root, table, writer.Lines, order);
        }

        return writer.Build(order);
    }

    /// <summary>The blocks of using directives in a file: the file's own and each namespace's.</summary>
    internal static IEnumerable<IReadOnlyList<UsingDirectiveSyntax>> UsingGroups(SyntaxNode root)
    {
        if (root is CompilationUnitSyntax unit && unit.Usings.Count > 0)
        {
            yield return unit.Usings;
        }

        var namespaces = root
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseNamespaceDeclarationSyntax>();
        foreach (var declaration in namespaces)
        {
            if (declaration.Usings.Count > 0)
            {
                yield return declaration.Usings;
            }
        }
    }

    private static void WriteTrivia(LineWriter writer, SyntaxTriviaList trivia, CSharpIgnoreOptions options)
    {
        foreach (var item in trivia)
        {
            switch (item.Kind())
            {
                case SyntaxKind.EndOfLineTrivia:
                    break;

                case SyntaxKind.WhitespaceTrivia:
                    writer.AppendWhitespace(item.Span.Start, item.Span.End);
                    break;

                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    WriteOrRemove(writer, item, (options & CSharpIgnoreOptions.Comments) != 0);
                    break;

                case SyntaxKind.RegionDirectiveTrivia:
                case SyntaxKind.EndRegionDirectiveTrivia:
                    WriteOrRemove(writer, item, (options & CSharpIgnoreOptions.Regions) != 0);
                    break;

                default:
                    writer.AppendTrivia(item.FullSpan.Start, item.FullSpan.End);
                    break;
            }
        }
    }

    private static void WriteOrRemove(LineWriter writer, SyntaxTrivia item, bool remove)
    {
        if (remove)
        {
            writer.MarkRemoved(item.FullSpan.Start, item.FullSpan.End);
        }
        else
        {
            writer.AppendTrivia(item.FullSpan.Start, item.FullSpan.End);
        }
    }

    /// <summary>Sorts each block of usings in place when every directive has a line of its own.</summary>
    private static void SortUsings(SyntaxNode root, LineTable table, StringBuilder[] lines, int[] order)
    {
        foreach (var group in UsingGroups(root))
        {
            var slots = new List<int>(group.Count);
            foreach (var directive in group)
            {
                var first = table.LineOf(directive.Span.Start);
                var last = table.LineOf(Math.Max(directive.Span.Start, directive.Span.End - 1));
                if (first != last || slots.Contains(first))
                {
                    slots.Clear();
                    break;
                }

                slots.Add(first);
            }

            if (slots.Count < 2)
            {
                continue;
            }

            var sorted = slots
                .OrderBy(line => UsingSortKey(lines[line].ToString()), StringComparer.Ordinal)
                .ThenBy(line => line)
                .ToArray();
            for (var i = 0; i < slots.Count; i++)
            {
                order[slots[i]] = sorted[i];
            }
        }
    }

    /// <summary>Sorts "using System;" before "using System.IO;" by leaving out the semicolon.</summary>
    private static string UsingSortKey(string line)
    {
        var trimmed = line.Trim();
        var semicolon = trimmed.IndexOf(';');
        return semicolon < 0 ? trimmed : trimmed.Substring(0, semicolon).TrimEnd() + "\u0000" + trimmed.Substring(semicolon);
    }

    /// <summary>Collects output text per original line.</summary>
    private sealed class LineWriter
    {
        private readonly string _text;
        private readonly LineTable _table;
        private readonly bool _formatting;
        private readonly bool[] _removed;

        public LineWriter(string text, LineTable table, bool formatting)
        {
            _text = text;
            _table = table;
            _formatting = formatting;
            Lines = new StringBuilder[table.Count];
            for (var i = 0; i < Lines.Length; i++)
            {
                Lines[i] = new StringBuilder();
            }

            _removed = new bool[table.Count];
        }

        public StringBuilder[] Lines { get; }

        public void AppendToken(int start, int end)
        {
            var first = true;
            foreach (var (line, segmentStart, segmentEnd) in _table.Segments(start, end))
            {
                if (_formatting && first)
                {
                    AppendSeparator(line);
                }

                Lines[line].Append(_text, segmentStart, segmentEnd - segmentStart);
                first = false;
            }
        }

        public void AppendWhitespace(int start, int end)
        {
            if (_formatting)
            {
                return;
            }

            AppendRaw(start, end);
        }

        public void AppendTrivia(int start, int end)
        {
            if (!_formatting)
            {
                AppendRaw(start, end);
                return;
            }

            foreach (var (line, segmentStart, segmentEnd) in _table.Segments(start, end))
            {
                var collapsed = CollapseWhitespace(_text.Substring(segmentStart, segmentEnd - segmentStart));
                if (collapsed.Length > 0)
                {
                    AppendSeparator(line);
                    Lines[line].Append(collapsed);
                }
            }
        }

        public void MarkRemoved(int start, int end)
        {
            foreach (var (line, _, _) in _table.Segments(start, end))
            {
                _removed[line] = true;
            }
        }

        public NormalizedText Build(int[] order)
        {
            var lines = new List<string>(Lines.Length);
            var originals = new List<int>(Lines.Length);
            for (var slot = 0; slot < order.Length; slot++)
            {
                var original = order[slot];
                var content = Lines[original].ToString();
                if (_removed[original])
                {
                    content = content.TrimEnd();
                }

                var dropped = content.Trim().Length == 0 && (_formatting || _removed[original]);
                if (dropped)
                {
                    continue;
                }

                lines.Add(content);
                originals.Add(original);
            }

            return new NormalizedText(lines.ToArray(), originals.ToArray(), Lines.Length);
        }

        private void AppendRaw(int start, int end)
        {
            foreach (var (line, segmentStart, segmentEnd) in _table.Segments(start, end))
            {
                Lines[line].Append(_text, segmentStart, segmentEnd - segmentStart);
            }
        }

        private void AppendSeparator(int line)
        {
            if (Lines[line].Length > 0)
            {
                Lines[line].Append(' ');
            }
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
