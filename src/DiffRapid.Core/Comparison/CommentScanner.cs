using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>Finds the comment ranges of every line of a document in one pass.</summary>
internal static class CommentScanner
{
    private static readonly TextSpan[] NoSpans = Array.Empty<TextSpan>();

    /// <summary>Returns, for each line, the character ranges that are comments.</summary>
    public static TextSpan[][] Scan(TextDocument document, CommentSyntax syntax, CancellationToken cancellationToken)
    {
        var result = new TextSpan[document.LineCount][];
        var openBlock = -1;
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            result[i] = ScanLine(document[i].Content, syntax, ref openBlock);
        }

        return result;
    }

    // openBlock is the index of the block comment still open from the previous line, or -1.
    private static TextSpan[] ScanLine(string content, CommentSyntax syntax, ref int openBlock)
    {
        List<TextSpan>? spans = null;
        var position = 0;
        if (openBlock >= 0)
        {
            position = CloseBlock(content, 0, 0, syntax.BlockComments[openBlock], ref openBlock, ref spans);
        }

        while (position < content.Length)
        {
            var c = content[position];
            if (syntax.QuotedStrings && (c == '"' || c == '\''))
            {
                position = SkipQuoted(content, position);
                continue;
            }

            if (StartsLineComment(content, position, syntax))
            {
                Add(ref spans, position, content.Length);
                break;
            }

            var block = BlockOpening(content, position, syntax);
            if (block >= 0)
            {
                var delimiters = syntax.BlockComments[block];
                openBlock = block;
                position = CloseBlock(content, position, position + delimiters.Open.Length, delimiters, ref openBlock, ref spans);
                continue;
            }

            position++;
        }

        return spans is null ? NoSpans : spans.ToArray();
    }

    // Adds the comment that starts at start and returns the position after its closing delimiter (or the line end).
    private static int CloseBlock(
        string content, int start, int searchFrom, BlockCommentDelimiters delimiters, ref int openBlock, ref List<TextSpan>? spans)
    {
        var close = content.IndexOf(delimiters.Close, searchFrom, StringComparison.Ordinal);
        if (close < 0)
        {
            Add(ref spans, start, content.Length);
            return content.Length;
        }

        var end = close + delimiters.Close.Length;
        Add(ref spans, start, end);
        openBlock = -1;
        return end;
    }

    private static int SkipQuoted(string content, int position)
    {
        var quote = content[position];
        var i = position + 1;
        while (i < content.Length)
        {
            if (content[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (content[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        return content.Length;
    }

    private static bool StartsLineComment(string content, int position, CommentSyntax syntax)
    {
        foreach (var prefix in syntax.LineComments)
        {
            if (prefix.Length > 0 && string.CompareOrdinal(content, position, prefix, 0, prefix.Length) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int BlockOpening(string content, int position, CommentSyntax syntax)
    {
        for (var b = 0; b < syntax.BlockComments.Count; b++)
        {
            var open = syntax.BlockComments[b].Open;
            if (open.Length > 0 && string.CompareOrdinal(content, position, open, 0, open.Length) == 0)
            {
                return b;
            }
        }

        return -1;
    }

    private static void Add(ref List<TextSpan>? spans, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        spans ??= new List<TextSpan>();
        spans.Add(new TextSpan(start, end - start));
    }
}
