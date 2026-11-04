using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Documents.Internal;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DiffRapid.Documents;

/// <summary>Which parts of a Word document <see cref="DocxText"/> writes besides the body.</summary>
public sealed record DocxTextOptions
{
    /// <summary>Headers, footers, footnotes and endnotes on; comments off.</summary>
    public static DocxTextOptions Default { get; } = new();

    /// <summary>Write each header and footer in a labelled section after the body.</summary>
    public bool IncludeHeadersAndFooters { get; init; } = true;

    /// <summary>Write footnotes and endnotes in labelled sections after the body.</summary>
    public bool IncludeNotes { get; init; } = true;

    /// <summary>Write review comments in labelled sections at the end.</summary>
    public bool IncludeComments { get; init; }
}

/// <summary>
/// Turns a Word document (.docx) into comparable text: one line per paragraph, headings marked with
/// '#' (one per level), list items with "- " indented by level, and table rows as tab-separated cells.
/// Tracked changes are shown as if accepted. Drawings, text boxes and field codes are left out.
/// </summary>
public static class DocxText
{
    private const int MaxStyleDepth = 16;

    /// <summary>Extracts the text of the Word document at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable Word document.</exception>
    public static string Extract(string path, DocxTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Extract(stream, options, cancellationToken);
    }

    /// <summary>Extracts the text of a Word document read from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable Word document.</exception>
    public static string Extract(Stream stream, DocxTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DocxTextOptions.Default;
        using var document = OpenXmlPackages.Open(stream, source => WordprocessingDocument.Open(source, false), "Word");
        var main = document.MainDocumentPart
            ?? throw new InvalidDataException("The Word document has no main document part.");
        var context = new Context(main, cancellationToken);
        var lines = new TextLines();
        var body = main.Document?.Body;
        WriteBlocks(lines, body, context);
        if (options.IncludeHeadersAndFooters && body is not null)
        {
            WriteHeadersAndFooters(lines, main, body, context);
        }

        if (options.IncludeNotes)
        {
            WriteNotes(lines, main, context);
        }

        if (options.IncludeComments)
        {
            WriteComments(lines, main, context);
        }

        return lines.ToString();
    }

    private static void WriteBlocks(TextLines lines, OpenXmlElement? container, Context context)
    {
        if (container is null)
        {
            return;
        }

        foreach (var element in container.ChildElements)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    lines.Add(FormatParagraph(paragraph, context));
                    break;
                case Table table:
                    WriteTable(lines, table, context);
                    break;
                case SdtBlock contentControl:
                    WriteBlocks(lines, contentControl.SdtContentBlock, context);
                    break;
                case CustomXmlBlock customXml:
                    WriteBlocks(lines, customXml, context);
                    break;
            }
        }
    }

    private static string FormatParagraph(Paragraph paragraph, Context context)
    {
        var text = new StringBuilder();
        AppendInlineText(text, paragraph);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var properties = paragraph.ParagraphProperties;
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        var headingLevel = HeadingLevel(properties, styleId, context);
        if (headingLevel > 0)
        {
            return new string('#', headingLevel) + " " + text;
        }

        var listLevel = ListLevel(properties, styleId, context);
        if (listLevel >= 0)
        {
            return new string(' ', 2 * listLevel) + "- " + text;
        }

        return text.ToString();
    }

    /// <summary>Appends the visible text of runs, following inserted text and skipping deleted text.</summary>
    private static void AppendInlineText(StringBuilder text, OpenXmlElement element)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case DeletedRun:
                case MoveFromRun:
                case ParagraphProperties:
                case RunProperties:
                case Drawing:
                case Picture:
                case EmbeddedObject:
                case AlternateContent:
                    break;
                case Text run:
                    text.Append(run.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break:
                case CarriageReturn:
                    text.Append('\n');
                    break;
                case NoBreakHyphen:
                    text.Append('-');
                    break;
                case FootnoteReference footnote:
                    text.Append("[^").Append(footnote.Id?.Value.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case EndnoteReference endnote:
                    text.Append("[^e").Append(endnote.Id?.Value.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                default:
                    AppendInlineText(text, child);
                    break;
            }
        }
    }

    private static void WriteTable(TextLines lines, Table table, Context context)
    {
        foreach (var row in Rows(table))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (row.TableRowProperties?.GetFirstChild<Deleted>() is not null)
            {
                continue;
            }

            lines.Add(string.Join("\t", Cells(row).Select(cell => CellText(cell, context))));
        }
    }

    private static IEnumerable<TableRow> Rows(OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            if (child is TableRow row)
            {
                yield return row;
            }
            else if (child is SdtRow or CustomXmlRow or SdtContentRow)
            {
                foreach (var nested in Rows(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<TableCell> Cells(OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            if (child is TableCell cell)
            {
                yield return cell;
            }
            else if (child is SdtCell or CustomXmlCell or SdtContentCell)
            {
                foreach (var nested in Cells(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>Joins a cell's lines with spaces so that each table row stays on one line.</summary>
    private static string CellText(TableCell cell, Context context)
    {
        var cellLines = new TextLines();
        WriteBlocks(cellLines, cell, context);
        var parts = cellLines.ToString()
            .Split(['\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Replace('\t', ' ').Trim())
            .Where(line => line.Length > 0);
        return string.Join(" ", parts);
    }

    private static int HeadingLevel(ParagraphProperties? properties, string? styleId, Context context)
    {
        var outline = properties?.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and < 9)
        {
            return outline.Value + 1;
        }

        return styleId is null ? 0 : context.StyleHeadingLevel(styleId);
    }

    private static int ListLevel(ParagraphProperties? properties, string? styleId, Context context)
    {
        var numbering = properties?.NumberingProperties;
        if (numbering is not null)
        {
            if (numbering.NumberingId?.Val?.Value == 0)
            {
                return -1;
            }

            if (numbering.NumberingId is not null)
            {
                return numbering.NumberingLevelReference?.Val?.Value ?? 0;
            }
        }

        return styleId is not null && context.StyleHasNumbering(styleId) ? 0 : -1;
    }

    private static void WriteHeadersAndFooters(TextLines lines, MainDocumentPart main, Body body, Context context)
    {
        var sections = body.Descendants<SectionProperties>().ToList();
        var written = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sections.Count; index++)
        {
            var suffix = sections.Count > 1 ? $", section {index + 1}" : string.Empty;
            foreach (var reference in sections[index].Elements<HeaderReference>())
            {
                WriteHeaderOrFooter(lines, main, reference.Id?.Value, $"[Header {Kind(reference.Type)}{suffix}]", written, context);
            }

            foreach (var reference in sections[index].Elements<FooterReference>())
            {
                WriteHeaderOrFooter(lines, main, reference.Id?.Value, $"[Footer {Kind(reference.Type)}{suffix}]", written, context);
            }
        }
    }

    private static string Kind(EnumValue<HeaderFooterValues>? type)
    {
        if (type?.Value == HeaderFooterValues.First)
        {
            return "first";
        }

        return type?.Value == HeaderFooterValues.Even ? "even" : "default";
    }

    private static void WriteHeaderOrFooter(
        TextLines lines,
        MainDocumentPart main,
        string? relationshipId,
        string label,
        HashSet<string> written,
        Context context)
    {
        if (relationshipId is null || !written.Add(relationshipId))
        {
            return;
        }

        var root = main.TryGetPartById(relationshipId, out var part) ? part.RootElement : null;
        if (root is null)
        {
            return;
        }

        lines.Add(label);
        WriteBlocks(lines, root, context);
    }

    private static void WriteNotes(TextLines lines, MainDocumentPart main, Context context)
    {
        foreach (var note in main.FootnotesPart?.Footnotes?.Elements<Footnote>() ?? [])
        {
            if (IsNormalNote(note.Type?.Value))
            {
                lines.Add($"[Footnote {note.Id?.Value.ToString(CultureInfo.InvariantCulture)}]");
                WriteBlocks(lines, note, context);
            }
        }

        foreach (var note in main.EndnotesPart?.Endnotes?.Elements<Endnote>() ?? [])
        {
            if (IsNormalNote(note.Type?.Value))
            {
                lines.Add($"[Endnote {note.Id?.Value.ToString(CultureInfo.InvariantCulture)}]");
                WriteBlocks(lines, note, context);
            }
        }
    }

    private static bool IsNormalNote(FootnoteEndnoteValues? type) =>
        type is null || type.Value == FootnoteEndnoteValues.Normal;

    private static void WriteComments(TextLines lines, MainDocumentPart main, Context context)
    {
        foreach (var comment in main.WordprocessingCommentsPart?.Comments?.Elements<Comment>() ?? [])
        {
            var author = comment.Author?.Value;
            var by = string.IsNullOrEmpty(author) ? string.Empty : " by " + author;
            lines.Add($"[Comment {comment.Id?.Value}{by}]");
            WriteBlocks(lines, comment, context);
        }
    }

    /// <summary>Per-document lookups: style headings and numbering, and the cancellation token.</summary>
    private sealed class Context
    {
        private readonly Dictionary<string, Style> _styles;
        private readonly Dictionary<string, int> _headingLevels = new(StringComparer.Ordinal);

        public Context(MainDocumentPart main, CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            _styles = new Dictionary<string, Style>(StringComparer.Ordinal);
            foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id && !_styles.ContainsKey(id))
                {
                    _styles.Add(id, style);
                }
            }
        }

        public CancellationToken CancellationToken { get; }

        /// <summary>Returns 1-9 for title and heading styles (directly or through "based on"), otherwise 0.</summary>
        public int StyleHeadingLevel(string styleId)
        {
            if (!_headingLevels.TryGetValue(styleId, out var level))
            {
                level = FindHeadingLevel(styleId, 0);
                _headingLevels[styleId] = level;
            }

            return level;
        }

        /// <summary>Returns whether a paragraph style (or a style it is based on) applies list numbering.</summary>
        public bool StyleHasNumbering(string styleId)
        {
            var current = styleId;
            for (var depth = 0; depth < MaxStyleDepth && current is not null; depth++)
            {
                if (!_styles.TryGetValue(current, out var style))
                {
                    return false;
                }

                var numbering = style.StyleParagraphProperties?.NumberingProperties;
                if (numbering?.NumberingId?.Val is { } id)
                {
                    return id.Value != 0;
                }

                current = style.BasedOn?.Val?.Value;
            }

            return false;
        }

        private int FindHeadingLevel(string styleId, int depth)
        {
            if (depth >= MaxStyleDepth)
            {
                return 0;
            }

            if (!_styles.TryGetValue(styleId, out var style))
            {
                return LevelFromName(styleId);
            }

            var byName = LevelFromName(style.StyleName?.Val?.Value ?? styleId);
            if (byName > 0)
            {
                return byName;
            }

            var outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (outline is >= 0 and < 9)
            {
                return outline.Value + 1;
            }

            var basedOn = style.BasedOn?.Val?.Value;
            return basedOn is null ? 0 : FindHeadingLevel(basedOn, depth + 1);
        }

        private static int LevelFromName(string name)
        {
            var compact = name.Replace(" ", string.Empty);
            if (compact.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            const string prefix = "heading";
            if (compact.Length == prefix.Length + 1
                && compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && compact[prefix.Length] is >= '1' and <= '9')
            {
                return compact[prefix.Length] - '0';
            }

            return 0;
        }
    }
}
