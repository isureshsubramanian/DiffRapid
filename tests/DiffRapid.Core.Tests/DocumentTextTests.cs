using DiffRapid.Cli;
using DiffRapid.Documents;
using DiffRapid.Documents.Internal;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DiffRapid.Core.Tests;

public sealed class DocumentTextTests
{
    [Fact]
    public void DocxWritesHeadingsListsTablesAndAcceptedChanges()
    {
        using var stream = Docx(body =>
        {
            body.Append(
                StyledParagraph("Heading1", "Introduction"),
                StyledParagraph("Heading2", "Scope"),
                Paragraph("Plain text"),
                ListParagraph(0, "First item"),
                ListParagraph(1, "Nested item"),
                new W.Paragraph(
                    Run("Kept "),
                    new W.InsertedRun(Run("added")) { Id = "1", Author = "A" },
                    new W.DeletedRun(new W.Run(new W.DeletedText("removed"))) { Id = "2", Author = "A" }),
                new W.Table(
                    new W.TableRow(Cell("Name"), Cell("Value")),
                    new W.TableRow(Cell("alpha"), Cell("1"))));
        });

        var text = DocxText.Extract(stream);

        Assert.Equal(
            "# Introduction\n## Scope\nPlain text\n- First item\n  - Nested item\nKept added\nName\tValue\nalpha\t1\n",
            text);
    }

    [Fact]
    public void DocxWritesHeadersFootersFootnotesAndOptionalComments()
    {
        var stream = Docx(body =>
        {
            body.Append(new W.Paragraph(Run("Body"), new W.Run(new W.FootnoteReference { Id = 1 })));
        }, AddNotesHeadersAndComments);

        var text = DocxText.Extract(stream);
        stream.Position = 0;
        var withComments = DocxText.Extract(stream, new DocxTextOptions { IncludeComments = true });
        stream.Position = 0;
        var bodyOnly = DocxText.Extract(stream, new DocxTextOptions { IncludeHeadersAndFooters = false, IncludeNotes = false });

        Assert.Equal("Body[^1]\n[Header default]\nPage header\n[Footer default]\nPage footer\n[Footnote 1]\nA note\n", text);
        Assert.EndsWith("[Comment 0 by Reviewer]\nPlease check\n", withComments);
        Assert.Equal("Body[^1]\n", bodyOnly);
    }

    [Fact]
    public void XlsxShowsValuesAsDisplayedAndFormulasOnRequest()
    {
        using var stream = Xlsx();

        var values = XlsxText.Extract(stream);
        stream.Position = 0;
        var formulas = XlsxText.Extract(stream, new XlsxTextOptions { CellContent = XlsxCellContent.Formulas });
        stream.Position = 0;
        var both = XlsxText.Extract(stream, new XlsxTextOptions { CellContent = XlsxCellContent.Both });

        Assert.Equal(
            "[Sheet: Data]\nName\tAmount\tWhen\tDone\n" +
            "alpha\t1.5\t2024-03-01\tTRUE\n\nbeta\t\t2024-03-01 12:30:00\t\t42\t43\t44\n" +
            "[Sheet: Secret (hidden)]\nhidden text\n",
            values);
        Assert.Contains("beta\t\t2024-03-01 12:30:00\t\t=B2*28\t=E4+1\t=F4+1\n", formulas);
        Assert.Contains("beta\t\t2024-03-01 12:30:00\t\t=B2*28 -> 42\t=E4+1 -> 43\t=F4+1 -> 44\n", both);
    }

    [Fact]
    public void XlsxExposesRowsPerSheetWithGapsKept()
    {
        using var stream = Xlsx();

        var sheets = XlsxText.ReadSheets(stream);

        Assert.Equal(["Data", "Secret"], sheets.Select(sheet => sheet.Name));
        Assert.Equal([false, true], sheets.Select(sheet => sheet.IsHidden));
        var rows = sheets[0].Rows;
        Assert.Equal(4, rows.Count);
        Assert.Empty(rows[2]);
        Assert.Equal(["beta", "", "2024-03-01 12:30:00", "", "42", "43", "44"], rows[3]);
    }

    [Fact]
    public void SharedFormulasShiftRelativeReferencesOnly()
    {
        Assert.Equal("B3+$A$1+C$2+'A1 sheet'!C3", CellReferences.ShiftFormula("A2+$A$1+B$2+'A1 sheet'!B2", 1, 1));
        Assert.Equal("SUM(B2:C3)&\"A1\"&LOG10(4)", CellReferences.ShiftFormula("SUM(A1:B2)&\"A1\"&LOG10(4)", 1, 1));
        Assert.Equal("#REF!+1", CellReferences.ShiftFormula("A1+1", -1, 0));
        Assert.Equal(28, CellReferences.ColumnIndex("AB7"));
        Assert.Equal("XFD", CellReferences.ColumnName(16384));
    }

    [Theory]
    [InlineData("yyyy-mm-dd", "Date")]
    [InlineData("d/m/yy h:mm", "DateTime")]
    [InlineData("[h]:mm:ss", "Time")]
    [InlineData("mmm", "Date")]
    [InlineData("0.00\"days\"", "None")]
    [InlineData("[Red]#,##0.00;[Blue]-#,##0.00", "None")]
    [InlineData("General", "None")]
    public void ClassifiesNumberFormats(string code, string expected)
    {
        Assert.Equal(expected, XlsxCellReader.ClassifyFormat(code).ToString());
    }

    [Fact]
    public void FormatsSerialDatesAndTimes()
    {
        Assert.Equal("2024-03-01", XlsxCellReader.FormatSerialDate(45352, DateKind.Date, date1904: false));
        Assert.Equal("2024-03-01", XlsxCellReader.FormatSerialDate(45352 - 1462, DateKind.Date, date1904: true));
        Assert.Equal("36:00:00", XlsxCellReader.FormatSerialDate(1.5, DateKind.Time, date1904: false));
        Assert.Null(XlsxCellReader.FormatSerialDate(-1, DateKind.Date, date1904: false));
    }

    [Fact]
    public void PptxWritesSlidesTitlesBodiesTablesAndNotes()
    {
        using var stream = Pptx();

        var text = PptxText.Extract(stream);

        Assert.Equal(
            "[Slide 1]\n# Welcome\n- Point one\n  - Detail\nFree text\nQ1\tQ2\n10\t20\n[Notes]\nSay hello\n" +
            "[Slide 2 (hidden)]\n# Second\n",
            text);
    }

    [Fact]
    public void UnpackersAreChosenByExtension()
    {
        Assert.Equal(["Word document", "Excel workbook", "PowerPoint presentation"], DocumentUnpackers.All.Select(unpacker => unpacker.Name));
        Assert.Equal("Excel workbook", DocumentUnpackers.Find(@"C:\data\Report.XLSM")?.Name);
        Assert.Null(DocumentUnpackers.Find("notes.txt"));
        Assert.Null(DocumentUnpackers.Find("README"));
        Assert.False(DocumentText.TryExtract("notes.txt", out var text));
        Assert.Equal(string.Empty, text);

        using var stream = Docx(body => body.Append(Paragraph("From the registry")));
        Assert.Equal("From the registry\n", DocumentUnpackers.Find("a.docx")!.Extract(stream));
    }

    [Fact]
    public void DamagedDocumentsAndCancellationAreReported()
    {
        using var garbage = new MemoryStream("not a zip file"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => DocxText.Extract(garbage));

        using var stream = Xlsx();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => XlsxText.ReadSheets(stream, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void CliDiffComparesDocumentsAsText()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.docx");
            var same = Path.Combine(directory, "same.docx");
            var right = Path.Combine(directory, "right.docx");
            var broken = Path.Combine(directory, "broken.docx");
            WriteFile(left, Docx(body => body.Append(Paragraph("Version one"))));
            WriteFile(same, Docx(body => body.Append(Paragraph("Version one"))));
            WriteFile(right, Docx(body => body.Append(Paragraph("Version two"))));
            File.WriteAllText(broken, "not a document");

            Assert.True(DocumentText.TryExtract(left, out var text));
            Assert.Equal("Version one\n", text);
            Assert.Equal(0, Program.Main(["diff", left, same, "--quiet"]));
            Assert.Equal(1, Program.Main(["diff", left, right, "--quiet"]));
            Assert.Equal(2, Program.Main(["diff", left, broken, "--quiet"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteFile(string path, MemoryStream stream)
    {
        using (stream)
        {
            File.WriteAllBytes(path, stream.ToArray());
        }
    }

    private static MemoryStream Docx(Action<W.Body> fillBody, Action<MainDocumentPart, W.Body>? extraParts = null)
    {
        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            AddStyles(main);
            fillBody(body);
            extraParts?.Invoke(main, body);
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "heading 1" }) { Type = W.StyleValues.Paragraph, StyleId = "Heading1" },
            new W.Style(new W.StyleName { Val = "heading 2" }) { Type = W.StyleValues.Paragraph, StyleId = "Heading2" });
    }

    private static void AddNotesHeadersAndComments(MainDocumentPart main, W.Body body)
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new W.Header(Paragraph("Page header"));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(Paragraph("Page footer"));
        body.Append(new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));

        var footnotes = main.AddNewPart<FootnotesPart>();
        footnotes.Footnotes = new W.Footnotes(
            new W.Footnote(Paragraph("")) { Type = W.FootnoteEndnoteValues.Separator, Id = -1 },
            new W.Footnote(new W.Paragraph(new W.Run(new W.FootnoteReferenceMark()), Run("A note"))) { Id = 1 });

        var comments = main.AddNewPart<WordprocessingCommentsPart>();
        comments.Comments = new W.Comments(new W.Comment(Paragraph("Please check")) { Id = "0", Author = "Reviewer" });
    }

    private static W.Run Run(string text) => new(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static W.Paragraph Paragraph(string text) => new(Run(text));

    private static W.Paragraph StyledParagraph(string styleId, string text) =>
        new(new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }), Run(text));

    private static W.Paragraph ListParagraph(int level, string text) =>
        new(
            new W.ParagraphProperties(new W.NumberingProperties(
                new W.NumberingLevelReference { Val = level },
                new W.NumberingId { Val = 1 })),
            Run(text));

    private static W.TableCell Cell(string text) => new(Paragraph(text));

    private static MemoryStream Xlsx()
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            AddSharedStringsAndStyles(workbookPart);
            var data = AddSheet(workbookPart, DataRows());
            var secret = AddSheet(workbookPart, new S.SheetData(new S.Row(TextCell("A1", "hidden text")) { RowIndex = 1 }));
            workbookPart.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "Data", SheetId = 1, Id = workbookPart.GetIdOfPart(data) },
                new S.Sheet { Name = "Secret", SheetId = 2, Id = workbookPart.GetIdOfPart(secret), State = S.SheetStateValues.Hidden }));
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddSharedStringsAndStyles(WorkbookPart workbookPart)
    {
        var strings = workbookPart.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new S.SharedStringTable(
            new S.SharedStringItem(new S.Text("Name")),
            new S.SharedStringItem(new S.Run(new S.Text("al")), new S.Run(new S.Text("pha"))));

        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new S.Stylesheet(
            new S.NumberingFormats(new S.NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy-mm-dd hh:mm" }),
            new S.CellFormats(
                new S.CellFormat { NumberFormatId = 0 },
                new S.CellFormat { NumberFormatId = 14 },
                new S.CellFormat { NumberFormatId = 164 }));
    }

    private static S.SheetData DataRows() =>
        new(
            new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                TextCell("B1", "Amount"),
                TextCell("C1", "When"),
                TextCell("D1", "Done")) { RowIndex = 1 },
            new S.Row(
                new S.Cell { CellReference = "A2", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("1") },
                new S.Cell { CellReference = "B2", CellValue = new S.CellValue("1.5") },
                new S.Cell { CellReference = "C2", StyleIndex = 1, CellValue = new S.CellValue("45352") },
                new S.Cell { CellReference = "D2", DataType = S.CellValues.Boolean, CellValue = new S.CellValue("1") }) { RowIndex = 2 },
            new S.Row(
                TextCell("A4", "beta"),
                new S.Cell { CellReference = "C4", StyleIndex = 2, CellValue = new S.CellValue("45352.520833333336") },
                new S.Cell
                {
                    CellReference = "E4",
                    CellFormula = new S.CellFormula("B2*28"),
                    CellValue = new S.CellValue("42"),
                },
                new S.Cell
                {
                    CellReference = "F4",
                    CellFormula = new S.CellFormula("E4+1") { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 0, Reference = "F4:G4" },
                    CellValue = new S.CellValue("43"),
                },
                new S.Cell
                {
                    CellReference = "G4",
                    CellFormula = new S.CellFormula { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 0 },
                    CellValue = new S.CellValue("44"),
                }) { RowIndex = 4 },
            new S.Row(new S.Cell { CellReference = "A6", StyleIndex = 1 }) { RowIndex = 6 });

    private static S.Cell TextCell(string reference, string text) =>
        new() { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) };

    private static WorksheetPart AddSheet(WorkbookPart workbookPart, S.SheetData data)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        part.Worksheet = new S.Worksheet(data);
        return part;
    }

    private static MemoryStream Pptx()
    {
        var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            var first = AddSlide(presentationPart, FirstSlideShapes());
            var notes = first.AddNewPart<NotesSlidePart>();
            notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(
                TextShape(1, P.PlaceholderValues.SlideImage),
                TextShape(2, P.PlaceholderValues.Body, TextParagraph("Say hello")))));
            var second = AddSlide(presentationPart, [TextShape(1, P.PlaceholderValues.Title, TextParagraph("Second"))]);
            second.Slide.Show = false;
            presentationPart.Presentation = new P.Presentation(new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(first) },
                new P.SlideId { Id = 257, RelationshipId = presentationPart.GetIdOfPart(second) }));
        }

        stream.Position = 0;
        return stream;
    }

    private static OpenXmlElement[] FirstSlideShapes() =>
    [
        TextShape(2, null, TextParagraph("Point one"), TextParagraph("Detail", level: 1), TextParagraph("")),
        TextShape(1, P.PlaceholderValues.Title, TextParagraph("Welcome")),
        new P.GroupShape(TextShape(3, placeholder: null, plain: true, TextParagraph("Free text"))),
        new P.GraphicFrame(new A.Graphic(new A.GraphicData(new A.Table(
            new A.TableRow(TableCell("Q1"), TableCell("Q2")),
            new A.TableRow(TableCell("10"), TableCell("20")))))),
    ];

    private static SlidePart AddSlide(PresentationPart presentationPart, OpenXmlElement[] shapes)
    {
        var part = presentationPart.AddNewPart<SlidePart>();
        part.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(shapes)));
        return part;
    }

    private static P.Shape TextShape(uint id, P.PlaceholderValues? placeholder, params A.Paragraph[] paragraphs) =>
        TextShape(id, placeholder, plain: false, paragraphs);

    private static P.Shape TextShape(uint id, P.PlaceholderValues? placeholder, bool plain, params A.Paragraph[] paragraphs)
    {
        var application = new P.ApplicationNonVisualDrawingProperties();
        if (!plain)
        {
            var placeholderShape = new P.PlaceholderShape();
            if (placeholder is not null)
            {
                placeholderShape.Type = placeholder.Value;
            }

            application.Append(placeholderShape);
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Shape " + id },
                new P.NonVisualShapeDrawingProperties(),
                application),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle()).AppendAll(paragraphs));
    }

    private static A.Paragraph TextParagraph(string text, int level = 0)
    {
        var paragraph = new A.Paragraph();
        if (level > 0)
        {
            paragraph.Append(new A.ParagraphProperties { Level = level });
        }

        if (text.Length > 0)
        {
            paragraph.Append(new A.Run(new A.Text(text)));
        }

        return paragraph;
    }

    private static A.TableCell TableCell(string text) =>
        new(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), TextParagraph(text)));
}

internal static class OpenXmlElementExtensions
{
    public static T AppendAll<T>(this T element, IEnumerable<OpenXmlElement> children)
        where T : OpenXmlElement
    {
        element.Append(children);
        return element;
    }
}
