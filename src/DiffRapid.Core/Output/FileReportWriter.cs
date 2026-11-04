using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Output;

/// <summary>Caller-supplied labels and layout for a report of one text comparison.</summary>
public sealed record FileReportOptions
{
    public string LeftLabel { get; init; } = "left";

    public string RightLabel { get; init; } = "right";

    public string Title { get; init; } = "File comparison";

    /// <summary>When the report was made; omitted from the output when null, so the output is reproducible.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>Unchanged lines kept around each difference; longer unchanged runs are collapsed.</summary>
    public int ContextLines { get; init; } = 3;
}

/// <summary>Writes reports of a text comparison: a side-by-side HTML page and a Markdown summary.</summary>
public static class FileReportWriter
{
    private const string PageStyle =
        "body{font:14px/1.45 system-ui,'Segoe UI',sans-serif;margin:2rem;color:#1f2328;background:#fff}"
        + "h1{font-size:1.5rem;margin:0 0 .75rem}dl{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem;margin:0 0 1rem}"
        + "dt{color:#656d76}dd{margin:0;overflow-wrap:anywhere}p.stats{margin:0 0 1rem}"
        + "@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}dt{color:#8d96a0}}";

    /// <summary>
    /// Writes one self-contained HTML page (inline styles, no scripts) that follows light and dark mode: a
    /// two-column view with line numbers and the changed words marked.
    /// </summary>
    public static void WriteHtml(
        TextDiffResult result,
        FileReportOptions options,
        TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        Validate(result, options, writer);
        writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>");
        writer.Write(SideBySideHtml.Encode(options.Title));
        writer.Write("</title><style>");
        writer.Write(PageStyle);
        writer.Write(SideBySideHtml.Style);
        writer.Write("</style></head><body><h1>");
        writer.Write(SideBySideHtml.Encode(options.Title));
        writer.Write("</h1><dl>");
        WriteFact(writer, "Left", options.LeftLabel);
        WriteFact(writer, "Right", options.RightLabel);
        if (options.GeneratedAt is { } generatedAt)
        {
            WriteFact(writer, "Generated", generatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        WriteFact(writer, "Differences", result.DifferenceCount.ToString(CultureInfo.InvariantCulture));
        writer.Write("</dl><p class=\"stats\">");
        writer.Write(SideBySideHtml.Encode(Summary(result)));
        writer.Write("</p>");
        SideBySideHtml.WriteTable(writer, result, options.LeftLabel, options.RightLabel, options.ContextLines, cancellationToken);
        writer.Write("</body></html>");
    }

    /// <summary>
    /// Writes Markdown for pasting into a pull request: the title, the two labels, a one-line summary and the
    /// unified diff in a <c>diff</c> code fence that is longer than any backtick run in the diff.
    /// </summary>
    public static void WriteMarkdown(TextDiffResult result, FileReportOptions options, TextWriter writer)
    {
        Validate(result, options, writer);
        writer.Write("## " + EscapeMarkdown(options.Title) + "\n\n");
        writer.Write("**Left:** " + CodeSpan(options.LeftLabel) + "  \n");
        writer.Write("**Right:** " + CodeSpan(options.RightLabel) + "\n\n");
        if (result.AreEqual)
        {
            writer.Write("The files are identical.\n");
            return;
        }

        writer.Write(Summary(result) + "\n\n");
        var diff = UnifiedDiffWriter.Write(result, options.LeftLabel, options.RightLabel, options.ContextLines);
        var fence = new string('`', Math.Max(3, LongestBacktickRun(diff) + 1));
        writer.Write(fence + "diff\n");
        writer.Write(diff);
        writer.Write(fence + "\n");
    }

    /// <summary>For example "2 differences: 1 changed, 3 added, 0 removed lines."</summary>
    internal static string Summary(TextDiffResult result)
    {
        if (result.AreEqual)
        {
            return "The files are identical.";
        }

        var stats = DiffLineStats.From(result);
        var count = result.DifferenceCount;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1}: {2} changed, {3} added, {4} removed lines.",
            count,
            count == 1 ? "difference" : "differences",
            stats.Changed,
            stats.Added,
            stats.Removed);
    }

    internal static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    // An inline code span delimited by more backticks than it contains, padded when it starts or ends with one.
    private static string CodeSpan(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ");
        var delimiter = new string('`', LongestBacktickRun(singleLine) + 1);
        var padding = singleLine.StartsWith("`", StringComparison.Ordinal) || singleLine.EndsWith("`", StringComparison.Ordinal) ? " " : string.Empty;
        return delimiter + padding + singleLine + padding + delimiter;
    }

    private static string EscapeMarkdown(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                builder.Append(' ');
                continue;
            }

            if ("\\`*_{}[]<>()#+-.!|~&".IndexOf(character) >= 0)
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void WriteFact(TextWriter writer, string label, string value)
    {
        writer.Write("<dt>" + label + "</dt><dd>" + SideBySideHtml.Encode(value) + "</dd>");
    }

    private static void Validate(TextDiffResult result, FileReportOptions options, TextWriter writer)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        if (options.ContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ContextLines cannot be negative.");
        }
    }
}
