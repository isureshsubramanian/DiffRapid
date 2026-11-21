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
