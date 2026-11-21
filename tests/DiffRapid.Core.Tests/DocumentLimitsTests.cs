using System.IO.Compression;
using DiffRapid.Documents;
using DiffRapid.Documents.Internal;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DiffRapid.Core.Tests;

/// <summary>Damaged or crafted Office files are reported as InvalidDataException instead of exhausting resources.</summary>
public sealed class DocumentLimitsTests
{
    private const int TooDeep = ExtractLimits.MaxDepth + 10;

    [Fact]
    public void DeeplyNestedContentControlsAreRejected()
    {
        using var stream = Docx(body =>
        {
            OpenXmlElement inner = new W.Paragraph(new W.Run(new W.Text("deep")));
            for (var level = 0; level < TooDeep; level++)
            {
                inner = new W.SdtBlock(new W.SdtContentBlock(inner));
            }

            body.Append(inner);
        });

        var error = Assert.Throws<InvalidDataException>(() => DocxText.Extract(stream));
        Assert.Contains("nested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedInlineElementsAreRejected()
    {
        using var stream = Docx(body =>
        {
            OpenXmlElement inner = new W.Run(new W.Text("deep"));
            for (var level = 0; level < TooDeep; level++)
            {
                inner = new W.Hyperlink(inner);
            }

            body.Append(new W.Paragraph(inner));
        });

        Assert.Throws<InvalidDataException>(() => DocxText.Extract(stream));
    }

    [Fact]
    public void ModeratelyNestedTablesAreRead()
    {
        using var stream = Docx(body =>
        {
            OpenXmlElement inner = new W.Paragraph(new W.Run(new W.Text("core")));
            for (var level = 0; level < 20; level++)
            {
                inner = new W.Table(new W.TableRow(new W.TableCell(inner)));
            }

            body.Append(inner);
        });

        Assert.Equal("core\n", DocxText.Extract(stream));
    }

    [Fact]
    public void DeeplyNestedGroupShapesAreRejected()
    {
        var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            OpenXmlElement inner = new P.GroupShape();
            for (var level = 0; level < TooDeep; level++)
            {
                inner = new P.GroupShape(inner);
            }

            var slide = presentationPart.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(inner)));
            presentationPart.Presentation = new P.Presentation(new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(slide) }));
        }

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => PptxText.Extract(stream));
    }

    [Theory]
    [InlineData(1_048_577u)]
    [InlineData(2_000_000_000u)]
    [InlineData(4_000_000_000u)]
    public void RowNumbersPastTheLastWorksheetRowAreRejected(uint rowIndex)
    {
        using var stream = Xlsx(new S.Row(new S.Cell { CellReference = "A1", CellValue = new S.CellValue("1") }) { RowIndex = rowIndex });

        var error = Assert.Throws<InvalidDataException>(() => XlsxText.ReadSheets(stream));
        Assert.Contains("past the last row", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastWorksheetRowIsStillRead()
    {
        using var stream = Xlsx(new S.Row(new S.Cell { CellReference = "A1048576", CellValue = new S.CellValue("7") }) { RowIndex = 1_048_576u });

        var rows = Assert.Single(XlsxText.ReadSheets(stream)).Rows;
        Assert.Equal(1_048_576, rows.Count);
        Assert.Equal(["7"], rows[^1]);
    }

    [Fact]
    public void DamageInALazilyLoadedPartIsReportedAsInvalidData()
    {
        using var source = Docx(body => body.Append(new W.Paragraph(new W.Run(new W.Text("text")))));
        var stream = new MemoryStream();
        source.CopyTo(stream);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry("word/document.xml")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open());
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>");
        }

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => DocxText.Extract(stream));
    }

    [Fact]
    public void ThousandsOfNestedContentControlsAreRejectedBeforeTheSdkLoadsThem()
    {
        // 1,500 levels of <w:sdt><w:sdtContent> overflowed the stack inside the SDK's recursive DOM load.
        using var source = Docx(body => body.Append(new W.Paragraph(new W.Run(new W.Text("text")))));
        using var stream = Replace(
            source,
            name => name == "word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                + Nest("<w:sdt><w:sdtContent>", "<w:p><w:r><w:t>deep</w:t></w:r></w:p>", "</w:sdtContent></w:sdt>", 1500)
                + "</w:body></w:document>");

        var error = Assert.Throws<InvalidDataException>(() => DocxText.Extract(stream));
        Assert.Contains("nested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThousandsOfNestedGroupShapesAreRejectedBeforeTheSdkLoadsThem()
    {
        var source = new MemoryStream();
        using (var document = PresentationDocument.Create(source, PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            var slide = presentationPart.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree()));
            presentationPart.Presentation = new P.Presentation(new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(slide) }));
        }

        source.Position = 0;
        using var stream = Replace(
            source,
            name => name.StartsWith("ppt/slides/", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal),
            "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree>"
                + Nest("<p:grpSp>", string.Empty, "</p:grpSp>", 3000)
                + "</p:spTree></p:cSld></p:sld>");

        Assert.Throws<InvalidDataException>(() => PptxText.Extract(stream));
    }

    [Fact]
    public void DeepNestingInAPartReadLaterIsRejected()
    {
        // The worksheet is read through OpenXmlReader long after the package opens; it is checked up front too.
        using var source = Xlsx(new S.Row(new S.Cell { CellReference = "A1", CellValue = new S.CellValue("1") }) { RowIndex = 1u });
        using var stream = Replace(
            source,
            name => name.StartsWith("xl/worksheets/", StringComparison.Ordinal),
            "<x:worksheet xmlns:x=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><x:sheetData><x:row r=\"1\">"
                + Nest("<x:c r=\"A1\"><x:is>", string.Empty, "</x:is></x:c>", 1500)
                + "</x:row></x:sheetData></x:worksheet>");

        Assert.Throws<InvalidDataException>(() => XlsxText.ReadSheets(stream));
    }

    [Fact]
    public void ADoctypeInAnyPartIsRejected()
    {
        using var source = Docx(body => body.Append(new W.Paragraph(new W.Run(new W.Text("text")))));
        using var stream = Replace(
            source,
            name => name == "word/document.xml",
            "<!DOCTYPE d [<!ENTITY e \"x\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");

        Assert.Throws<InvalidDataException>(() => DocxText.Extract(stream));
    }

    [Fact]
    public void BinaryAndImagePartsAreNotScannedAsXml()
    {
        // Excel saves binary printer settings with a page setup; Word may hold an SVG with a DOCTYPE. Neither is XML
        // the SDK parses, so neither makes the document unreadable.
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            var sheet = workbookPart.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell { CellValue = new S.CellValue("7"), DataType = S.CellValues.Number })));
            workbookPart.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "Data", SheetId = 1, Id = workbookPart.GetIdOfPart(sheet) }));
            var printer = sheet.AddNewPart<SpreadsheetPrinterSettingsPart>();
            using var printerData = new MemoryStream([0x00, 0x01, 0x3C, 0x00, 0xFF]);
            printer.FeedData(printerData);
            var drawing = sheet.AddNewPart<DrawingsPart>();
            drawing.WorksheetDrawing = new DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing();
            var image = drawing.AddImagePart(ImagePartType.Svg);
            using var svg = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE svg><svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
            image.FeedData(svg);
        }

        stream.Position = 0;
        Assert.Contains("7", XlsxText.Extract(stream), StringComparison.Ordinal);
    }

    [Fact]
    public void PartsAreOpenedWithACharacterLimit()
    {
        using var stream = Docx(body => body.Append(new W.Paragraph(new W.Run(new W.Text(new string('x', 500))))));
        long? limit = null;
        var text = OpenXmlPackages.Read(
            stream,
            (source, settings) =>
            {
                limit = settings.MaxCharactersInPart;
                return WordprocessingDocument.Open(source, false, settings);
            },
            "Word",
            document => document.MainDocumentPart!.Document!.InnerText);
        Assert.Equal(OpenXmlPackages.MaxCharactersInPart, limit);
        Assert.Equal(500, text.Length);

        // A part past the limit fails as damaged data, however late it is loaded.
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => OpenXmlPackages.Read(
            stream,
            (source, _) => WordprocessingDocument.Open(source, false, new OpenSettings { MaxCharactersInPart = 100 }),
            "Word",
            document => document.MainDocumentPart!.Document!.InnerText));
    }

    [Fact]
    public void TextBudgetChecksBeforeAdding()
    {
        var used = ExtractLimits.MaxTextLength - 10;
        ExtractLimits.AddText(ref used, 10);
        Assert.Equal(ExtractLimits.MaxTextLength, used);
        Assert.Throws<InvalidDataException>(() =>
        {
            var full = ExtractLimits.MaxTextLength;
            ExtractLimits.AddText(ref full, 1);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var some = 1L;
            ExtractLimits.AddText(ref some, long.MaxValue);
        });
    }

    private static string Nest(string open, string inner, string close, int levels)
    {
        var text = new System.Text.StringBuilder();
        for (var level = 0; level < levels; level++)
        {
            text.Append(open);
        }

        text.Append(inner);
        for (var level = 0; level < levels; level++)
        {
            text.Append(close);
        }

        return text.ToString();
    }

    // Copies a package and rewrites the first entry whose name matches with raw XML, so the SDK never builds (or writes) its DOM.
    private static MemoryStream Replace(Stream source, Func<string, bool> matches, string xml)
    {
        var stream = new MemoryStream();
        source.CopyTo(stream);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.Entries.First(candidate => matches(candidate.FullName));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(xml);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Docx(Action<W.Body> fillBody)
    {
        var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            fillBody(body);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Xlsx(params S.Row[] rows)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            var sheet = workbookPart.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(rows));
            workbookPart.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "Data", SheetId = 1, Id = workbookPart.GetIdOfPart(sheet) }));
        }

        stream.Position = 0;
        return stream;
    }
}
