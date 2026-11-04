using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>How the changed files are compared and shown in a folder report with embedded diffs.</summary>
public sealed record FolderDiffReportOptions
{
    public CompareOptions TextOptions { get; init; } = CompareOptions.Default;

    /// <summary>Unchanged lines kept around each difference; longer unchanged runs are collapsed.</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Files larger than this on either side are listed without a diff.</summary>
    public long MaxFileSize { get; init; } = 16L * 1024 * 1024;
}

/// <summary>
/// Writes the HTML folder report of <see cref="FolderReportWriter"/> followed by a side-by-side diff of every
/// changed file, each with an anchor and listed in a table of contents.
/// </summary>
public static class FolderDiffReportWriter
{
    private const string SectionStyle =
        "h2{font-size:1.25rem;margin:2rem 0 .75rem}h3{font-size:1rem;margin:1.5rem 0 .5rem;overflow-wrap:anywhere}"
        + "ol.toc{margin:0 0 1rem;padding-left:1.5rem}ol.toc a{overflow-wrap:anywhere}p.note{color:#656d76;margin:0 0 1rem}"
        + "a{color:#0969da}@media(prefers-color-scheme:dark){a{color:#4493f8}p.note{color:#8d96a0}}";

    private const string StyleEnd = "</style>";
    private const string BodyEnd = "</body>";

    /// <summary>
    /// Writes one self-contained page. Files are read from the comparison's roots one at a time; binary, missing,
    /// unreadable and oversized files get a note instead of a diff.
    /// </summary>
    public static void WriteHtml(
        FolderCompareResult result,
        FolderReportOptions reportOptions,
        FolderDiffReportOptions diffOptions,
        TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        if (diffOptions is null)
        {
            throw new ArgumentNullException(nameof(diffOptions));
        }

        if (diffOptions.ContextLines < 0 || diffOptions.MaxFileSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diffOptions));
        }

        using var buffer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        FolderReportWriter.WriteHtml(result, reportOptions, buffer);
        var page = buffer.ToString();
        var styleEnd = page.IndexOf(StyleEnd, StringComparison.Ordinal);
        var bodyEnd = page.LastIndexOf(BodyEnd, StringComparison.Ordinal);

        writer.Write(page.Substring(0, styleEnd));
        writer.Write(SideBySideHtml.Style);
        writer.Write(SectionStyle);
        writer.Write(page.Substring(styleEnd, bodyEnd - styleEnd));
        WriteDiffs(result, diffOptions, writer, cancellationToken);
        writer.Write(page.Substring(bodyEnd));
    }

    private static void WriteDiffs(
        FolderCompareResult result,
        FolderDiffReportOptions options,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var changed = ChangedFiles(result);
        writer.Write("<h2>Changed files</h2>");
        if (changed.Count == 0)
        {
            writer.Write("<p class=\"note\">No file has changed content.</p>");
            return;
        }

        writer.Write("<ol class=\"toc\">");
        for (var index = 0; index < changed.Count; index++)
        {
            writer.Write("<li><a href=\"#" + Anchor(index) + "\">" + SideBySideHtml.Encode(changed[index].RelativePath) + "</a></li>");
        }

        writer.Write("</ol>");
        for (var index = 0; index < changed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = changed[index];
            writer.Write("<section id=\"" + Anchor(index) + "\"><h3>" + SideBySideHtml.Encode(item.RelativePath) + "</h3>");
            WriteFileDiff(result, item, options, writer, cancellationToken);
            writer.Write("</section>");
        }
    }

    private static void WriteFileDiff(
        FolderCompareResult result,
        FolderCompareItem item,
        FolderDiffReportOptions options,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        if (item.Left.Size > options.MaxFileSize || item.Right.Size > options.MaxFileSize)
        {
            WriteNote(writer, "Too large to show here.");
            return;
        }

        DecodedText left;
        DecodedText right;
        try
        {
            left = TextFileReader.ReadFile(Path.Combine(result.LeftRoot, item.RelativePath));
            right = TextFileReader.ReadFile(Path.Combine(result.RightRoot, item.RelativePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteNote(writer, "Could not read the file: " + exception.Message);
            return;
        }

        if (left.IsBinary || right.IsBinary)
        {
            WriteNote(writer, "Binary files differ.");
            return;
        }

        var diff = TextComparer.Compare(TextDocument.Parse(left.Text), TextDocument.Parse(right.Text), options.TextOptions, cancellationToken);
        WriteNote(writer, FileReportWriter.Summary(diff));
        if (diff.Hunks.Count > 0)
        {
            SideBySideHtml.WriteTable(writer, diff, "Left", "Right", options.ContextLines, cancellationToken);
        }
    }

    private static IReadOnlyList<FolderCompareItem> ChangedFiles(FolderCompareResult result) =>
        result.Items
            .Where(i => i.Kind == FolderItemKind.File && i.Status == FolderCompareStatus.Different)
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RelativePath, StringComparer.Ordinal)
            .ToList();

    private static string Anchor(int index) => "diff-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteNote(TextWriter writer, string text)
    {
        writer.Write("<p class=\"note\">" + SideBySideHtml.Encode(text) + "</p>");
    }
}
