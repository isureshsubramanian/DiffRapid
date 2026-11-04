using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Documents.Internal;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DiffRapid.Documents;

/// <summary>
/// Turns a PowerPoint presentation (.pptx) into comparable text: slides in order under a "[Slide n]" label,
/// the title marked with "# ", then the text of every shape in drawing order (paragraphs of content
/// placeholders as "- " items indented by level, tables as tab-separated rows), then the speaker notes
/// under "[Notes]".
/// </summary>
public static class PptxText
{
    /// <summary>Extracts the text of the presentation at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable PowerPoint presentation.</exception>
    public static string Extract(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Extract(stream, cancellationToken);
    }

    /// <summary>Extracts the text of a presentation read from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable PowerPoint presentation.</exception>
    public static string Extract(Stream stream, CancellationToken cancellationToken = default)
    {
        using var document = OpenXmlPackages.Open(stream, source => PresentationDocument.Open(source, false), "PowerPoint");
        var presentationPart = document.PresentationPart
            ?? throw new InvalidDataException("The PowerPoint presentation has no presentation part.");
        var lines = new TextLines();
        var number = 0;
        foreach (var slideId in presentationPart.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationshipId = slideId.RelationshipId?.Value;
            if (relationshipId is null || !presentationPart.TryGetPartById(relationshipId, out var part) || part is not SlidePart slidePart)
            {
                continue;
            }

            number++;
            WriteSlide(lines, slidePart, number);
        }

        return lines.ToString();
    }

    private static void WriteSlide(TextLines lines, SlidePart slidePart, int number)
    {
        var slide = slidePart.Slide;
        var hidden = slide?.Show?.Value == false ? " (hidden)" : string.Empty;
        lines.Add($"[Slide {number}{hidden}]");
        var tree = slide?.CommonSlideData?.ShapeTree;
        if (tree is not null)
        {
            var title = tree.Descendants<P.Shape>().FirstOrDefault(IsTitle);
            if (title is not null)
            {
                lines.Add("# " + string.Join(" ", Paragraphs(title.TextBody).Select(ParagraphText).Where(text => text.Length > 0)));
            }

            WriteShapes(lines, tree, title);
        }

        WriteNotes(lines, slidePart.NotesSlidePart);
    }

    private static void WriteShapes(TextLines lines, OpenXmlElement container, P.Shape? title)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case P.Shape shape when shape != title:
                    WriteShapeText(lines, shape);
                    break;
                case P.GroupShape group:
                    WriteShapes(lines, group, title);
                    break;
                case P.GraphicFrame frame:
                    WriteTables(lines, frame);
                    break;
            }
        }
    }

    private static void WriteShapeText(TextLines lines, P.Shape shape)
    {
        var asItems = IsContentPlaceholder(shape);
        foreach (var paragraph in Paragraphs(shape.TextBody))
        {
            var text = ParagraphText(paragraph);
            if (text.Length == 0)
            {
                continue;
            }

            var level = paragraph.ParagraphProperties?.Level?.Value ?? 0;
            var indent = new string(' ', 2 * level);
            lines.Add(asItems ? indent + "- " + text : indent + text);
        }
    }

    private static void WriteTables(TextLines lines, P.GraphicFrame frame)
    {
        foreach (var table in frame.Descendants<A.Table>())
        {
            foreach (var row in table.Elements<A.TableRow>())
            {
                var cells = row.Elements<A.TableCell>()
                    .Select(cell => string.Join(" ", Paragraphs(cell.TextBody).Select(ParagraphText).Where(text => text.Length > 0)));
                lines.Add(string.Join("\t", cells));
            }
        }
    }

    private static void WriteNotes(TextLines lines, NotesSlidePart? notesPart)
    {
        var tree = notesPart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return;
        }

        var notes = tree.Descendants<P.Shape>()
            .Where(shape => PlaceholderType(shape) == P.PlaceholderValues.Body)
            .SelectMany(shape => Paragraphs(shape.TextBody))
            .Select(ParagraphText)
            .Where(text => text.Length > 0)
            .ToList();
        if (notes.Count == 0)
        {
            return;
        }

        lines.Add("[Notes]");
        foreach (var note in notes)
        {
            lines.Add(note);
        }
    }

    private static IEnumerable<A.Paragraph> Paragraphs(OpenXmlElement? textBody) =>
        textBody?.Elements<A.Paragraph>() ?? [];

    /// <summary>Joins the paragraph's runs and fields; a line break becomes a space and tabs stay tabs.</summary>
    private static string ParagraphText(A.Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run:
                    text.Append(run.Text?.Text);
                    break;
                case A.Field field:
                    text.Append(field.Text?.Text);
                    break;
                case A.Break:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Replace('\t', ' ').Trim();
    }

    private static bool IsTitle(P.Shape shape)
    {
        var type = PlaceholderType(shape);
        return type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle;
    }

    /// <summary>Body placeholders and untyped (content) placeholders hold bulleted text.</summary>
    private static bool IsContentPlaceholder(P.Shape shape)
    {
        var placeholder = Placeholder(shape);
        if (placeholder is null)
        {
            return false;
        }

        var type = placeholder.Type?.Value;
        return type is null || type == P.PlaceholderValues.Body || type == P.PlaceholderValues.Object;
    }

    private static P.PlaceholderValues? PlaceholderType(P.Shape shape)
    {
        var placeholder = Placeholder(shape);
        if (placeholder is null)
        {
            return null;
        }

        return placeholder.Type?.Value ?? P.PlaceholderValues.Object;
    }

    private static P.PlaceholderShape? Placeholder(P.Shape shape) =>
        shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
}
