using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>Line counts of the important differences in a comparison.</summary>
internal readonly record struct DiffLineStats(int Changed, int Added, int Removed)
{
    public static DiffLineStats From(TextDiffResult result)
    {
        var changed = 0;
        var added = 0;
        var removed = 0;
        foreach (var hunk in result.ImportantHunks)
        {
            var paired = Math.Min(hunk.LeftCount, hunk.RightCount);
            changed += paired;
            added += hunk.RightCount - paired;
            removed += hunk.LeftCount - paired;
        }

        return new DiffLineStats(changed, added, removed);
    }
}

/// <summary>
/// Writes a two-column HTML table of a text comparison. Runs of unchanged lines longer than twice the
/// context are collapsed to a single row; output is streamed row by row.
/// </summary>
internal static class SideBySideHtml
{
    /// <summary>Lines longer than this are shown without inline marks, to bound the word diff's cost.</summary>
    internal const int MaxInlineLineLength = 10000;

    internal const string Style =
        ".sbs{border-collapse:collapse;width:100%;table-layout:fixed;margin:0 0 1.5rem;font:12px/1.45 ui-monospace,'Cascadia Mono',Consolas,monospace}"
        + ".sbs col.ln{width:4.5em}"
        + ".sbs th{font:600 13px/1.4 system-ui,'Segoe UI',sans-serif;text-align:left;padding:.35rem .5rem;background:#f6f8fa;border:1px solid #d0d7de;overflow-wrap:anywhere;white-space:normal}"
        + ".sbs td{padding:0 .5rem;border:0;vertical-align:top}"
        + ".sbs td.ln{text-align:right;color:#656d76;border-right:1px solid #d0d7de;white-space:nowrap;user-select:none}"
        + ".sbs td.tx{white-space:pre-wrap;overflow-wrap:anywhere}"
        + ".sbs td.o{background:#ffebe9}.sbs td.n{background:#e6ffec}.sbs td.e{background:#f6f8fa}.sbs td.i{background:#f6f8fa;color:#656d76}"
        + ".sbs td.o mark{background:#ffc1bc;color:inherit}.sbs td.n mark{background:#abf2bc;color:inherit}"
        + ".sbs tr.gap td{padding:.2rem .5rem;text-align:center;color:#656d76;background:#ddf4ff;font-style:italic}"
        + "@media(prefers-color-scheme:dark){.sbs th{background:#161b22;border-color:#30363d}.sbs td.ln{color:#8d96a0;border-color:#30363d}"
        + ".sbs td.o{background:#3c1618}.sbs td.n{background:#12261e}.sbs td.e,.sbs td.i{background:#161b22}.sbs td.i{color:#8d96a0}"
        + ".sbs td.o mark{background:#8e1519}.sbs td.n mark{background:#1c6b34}.sbs tr.gap td{background:#121d2f;color:#8d96a0}}";

    /// <summary>Writes the table. Ignored differences are shown muted; only unchanged runs are collapsed.</summary>
    public static void WriteTable(
        TextWriter writer,
        TextDiffResult result,
        string leftLabel,
        string rightLabel,
        int contextLines,
        CancellationToken cancellationToken)
    {
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines));
        }

        writer.Write("<table class=\"sbs\"><colgroup><col class=\"ln\"><col><col class=\"ln\"><col></colgroup><thead><tr><th colspan=\"2\">");
        writer.Write(Encode(leftLabel));
        writer.Write("</th><th colspan=\"2\">");
        writer.Write(Encode(rightLabel));
        writer.Write("</th></tr></thead><tbody>");

        var table = new TableWriter(writer, result, contextLines, cancellationToken);
        table.WriteRows();
        writer.Write("</tbody></table>");
    }

    internal static string Encode(string text) => WebUtility.HtmlEncode(text);

    private sealed class TableWriter
    {
        private readonly TextWriter _writer;
        private readonly TextDiffResult _result;
        private readonly int _contextLines;
        private readonly CancellationToken _cancellationToken;

        public TableWriter(TextWriter writer, TextDiffResult result, int contextLines, CancellationToken cancellationToken)
        {
            _writer = writer;
            _result = result;
            _contextLines = contextLines;
            _cancellationToken = cancellationToken;
        }

        public void WriteRows()
        {
            var hunks = _result.Hunks;
            var left = 0;
            var right = 0;
            for (var index = 0; index < hunks.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var hunk = hunks[index];
                var equalCount = Math.Min(hunk.LeftStart - left, hunk.RightStart - right);
                WriteUnchanged(left, right, equalCount, isLeading: index == 0, isTrailing: false);
                WriteHunk(hunk);
                left = hunk.LeftEnd;
                right = hunk.RightEnd;
            }

            var remaining = Math.Min(_result.Left.LineCount - left, _result.Right.LineCount - right);
            WriteUnchanged(left, right, remaining, isLeading: hunks.Count == 0, isTrailing: true);
        }

        private void WriteUnchanged(int left, int right, int count, bool isLeading, bool isTrailing)
        {
            if (count <= 0)
            {
                return;
            }

            long head = isLeading ? 0 : _contextLines;
            long tail = isTrailing ? 0 : _contextLines;
            if (head + tail >= count)
            {
                head = count;
                tail = 0;
            }

            for (var i = 0; i < head; i++)
            {
                WriteRow("", left + i, "", right + i, "", null);
            }

            var hidden = count - head - tail;
            if (hidden > 0)
            {
                _writer.Write("<tr class=\"gap\"><td colspan=\"4\">&#8943; ");
                _writer.Write(hidden.ToString(CultureInfo.InvariantCulture));
                _writer.Write(hidden == 1 ? " unchanged line" : " unchanged lines");
                _writer.Write(" &#8943;</td></tr>");
            }

            for (var i = count - (int)tail; i < count; i++)
            {
                WriteRow("", left + i, "", right + i, "", null);
            }

            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void WriteHunk(DiffHunk hunk)
        {
            var ignored = hunk.Significance == DiffSignificance.Ignored;
            var oldClass = ignored ? "i" : "o";
            var newClass = ignored ? "i" : "n";
            var paired = Math.Min(hunk.LeftCount, hunk.RightCount);
            for (var i = 0; i < paired; i++)
            {
                var marks = ignored ? null : InlineMarks(_result.Left[hunk.LeftStart + i].Content, _result.Right[hunk.RightStart + i].Content);
                WriteRow(oldClass, hunk.LeftStart + i, newClass, hunk.RightStart + i, ignored ? "ign" : "chg", marks);
            }

            for (var i = paired; i < hunk.LeftCount; i++)
            {
                WriteRow(oldClass, hunk.LeftStart + i, "e", null, ignored ? "ign" : "del", null);
            }

            for (var i = paired; i < hunk.RightCount; i++)
            {
                WriteRow("e", null, newClass, hunk.RightStart + i, ignored ? "ign" : "add", null);
            }
        }

        private InlineDiff? InlineMarks(string left, string right)
        {
            if (left.Length > MaxInlineLineLength || right.Length > MaxInlineLineLength)
            {
                return null;
            }

            return InlineDiffer.Compare(left, right, _result.Options);
        }

        private void WriteRow(string leftClass, int? leftLine, string rightClass, int? rightLine, string rowClass, InlineDiff? marks)
        {
            _writer.Write(rowClass.Length == 0 ? "<tr>" : "<tr class=\"" + rowClass + "\">");
            WriteCell(leftClass, leftLine, _result.Left, marks?.Left);
            WriteCell(rightClass, rightLine, _result.Right, marks?.Right);
            _writer.Write("</tr>");
        }

        private void WriteCell(string cellClass, int? line, TextDocument document, IReadOnlyList<TextSpan>? spans)
        {
            _writer.Write("<td class=\"ln\">");
            if (line is { } number)
            {
                _writer.Write((number + 1).ToString(CultureInfo.InvariantCulture));
            }

            _writer.Write(cellClass.Length == 0 ? "</td><td class=\"tx\">" : "</td><td class=\"tx " + cellClass + "\">");
            if (line is { } index)
            {
                WriteMarkedText(document[index].Content, spans);
            }

            _writer.Write("</td>");
        }

        private void WriteMarkedText(string text, IReadOnlyList<TextSpan>? spans)
        {
            if (spans is null || spans.Count == 0)
            {
                _writer.Write(Encode(text));
                return;
            }

            var position = 0;
            foreach (var span in spans)
            {
                _writer.Write(Encode(text.Substring(position, span.Start - position)));
                _writer.Write("<mark>");
                _writer.Write(Encode(text.Substring(span.Start, span.Length)));
                _writer.Write("</mark>");
                position = span.End;
            }

            _writer.Write(Encode(text.Substring(position)));
        }
    }
}
