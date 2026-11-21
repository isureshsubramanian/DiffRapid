using DiffRapid.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace DiffRapid.Core.Tests;

/// <summary>The by-extension document readers pass the caller's cancellation token through.</summary>
public sealed class DocumentCancellationTests
{
    [Fact]
    public void UnpackersAndTryExtractPassTheCancellationToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = Xlsx(new S.Row(new S.Cell { CellReference = "A1", CellValue = new S.CellValue("1") }) { RowIndex = 1 });
        var unpacker = DocumentUnpackers.Find("book.xlsx")!;
        Assert.ThrowsAny<OperationCanceledException>(() => unpacker.ExtractText(stream, cancelled.Token));

        stream.Position = 0;
        Assert.Equal("[Sheet: Data]\n1\n", unpacker.ExtractText(stream, CancellationToken.None));

        var replaced = unpacker with { Extract = _ => "replaced" };
        Assert.Equal("replaced", replaced.ExtractText(stream, CancellationToken.None));

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            File.WriteAllBytes(path, stream.ToArray());
            Assert.ThrowsAny<OperationCanceledException>(() => DocumentText.TryExtract(path, out _, cancelled.Token));
        }
        finally
        {
            File.Delete(path);
        }
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
