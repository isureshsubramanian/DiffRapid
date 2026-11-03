using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace DiffRapid.Core.Folders;

/// <summary>Caller-supplied metadata and row selection for a folder report.</summary>
public sealed record FolderReportOptions
{
    public string LeftRoot { get; init; } = string.Empty;

    public string RightRoot { get; init; } = string.Empty;

    public string Title { get; init; } = "Folder comparison";

    /// <summary>When the report was made; supplied by the caller so the output is reproducible.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    public string MethodLabel { get; init; } = string.Empty;

    /// <summary>List identical files too; by default only differences are listed.</summary>
    public bool IncludeIdentical { get; init; }
}

/// <summary>
/// Writes folder comparison reports. Rows are files, plus folders that exist on one side or cannot be
/// compared, sorted by path; excluded entries never appear.
/// </summary>
public static class FolderReportWriter
{
    private const string Style =
        "body{font:14px/1.45 system-ui,'Segoe UI',sans-serif;margin:2rem;color:#1f2328;background:#fff}"
        + "h1{font-size:1.5rem;margin:0 0 .75rem}dl{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem;margin:0 0 1.25rem}"
        + "dt{color:#656d76}dd{margin:0;overflow-wrap:anywhere}"
        + ".summary{display:flex;flex-wrap:wrap;gap:.6rem;margin:0 0 1.25rem;padding:0;list-style:none}"
        + ".summary li{border:1px solid #d0d7de;border-radius:6px;padding:.4rem .8rem}.summary b{font-size:1.15rem;margin-right:.35rem}"
        + "table{border-collapse:collapse;width:100%}th,td{padding:.35rem .6rem;border-bottom:1px solid #d0d7de;text-align:left;vertical-align:top}"
        + "th{font-weight:600;background:#f6f8fa}td{overflow-wrap:anywhere;white-space:pre-wrap}td.n{text-align:right;white-space:nowrap}"
        + ".pill{display:inline-block;border-radius:1rem;padding:0 .55rem;font-size:.85em;white-space:nowrap}"
        + ".Identical{background:#dafbe1;color:#116329}.Different{background:#fff1c2;color:#7d4e00}"
        + ".LeftOnly{background:#ffe2e0;color:#a40e26}.RightOnly{background:#d8f5e3;color:#0b5a2b}"
        + ".TypeConflict,.Error{background:#eadcff;color:#5a32a3}"
        + "@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}dt{color:#8d96a0}"
        + "th{background:#161b22}th,td,.summary li{border-color:#30363d}"
        + ".Identical{background:#12361f;color:#7ee2a0}.Different{background:#3b2e05;color:#f2cc60}"
        + ".LeftOnly{background:#43151a;color:#ff9b9b}.RightOnly{background:#0f3321;color:#7ee2a0}"
        + ".TypeConflict,.Error{background:#2e2150;color:#d2b8ff}}";

    /// <summary>Writes one self-contained HTML page (inline styles, no scripts) that follows light and dark mode.</summary>
    public static void WriteHtml(FolderCompareResult result, FolderReportOptions options, TextWriter writer)
    {
        Validate(result, options, writer);
        var rows = Rows(result, options);

        writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>");
        writer.Write(Encode(options.Title));
        writer.Write("</title><style>");
        writer.Write(Style);
        writer.Write("</style></head><body><h1>");
        writer.Write(Encode(options.Title));
        writer.Write("</h1><dl>");
        var facts = new[]
        {
            ("Left", options.LeftRoot),
            ("Right", options.RightRoot),
            ("Generated", options.GeneratedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            ("Compared by", options.MethodLabel),
        };
        foreach (var (label, value) in facts)
        {
            writer.Write("<dt>" + label + "</dt><dd>" + Encode(value) + "</dd>");
        }

        writer.Write("</dl><ul class=\"summary\">");
        foreach (FolderCompareStatus status in Enum.GetValues(typeof(FolderCompareStatus)))
        {
            if (status == FolderCompareStatus.Skipped || (status == FolderCompareStatus.Identical && !options.IncludeIdentical))
            {
                continue;
            }

            var count = rows.Count(i => i.Status == status);
            writer.Write("<li><b>" + count.ToString(CultureInfo.InvariantCulture) + "</b>" + Label(status) + "</li>");
        }

        writer.Write("</ul><table><thead><tr><th>Path</th><th>Status</th><th>Left size</th><th>Left modified (UTC)</th>"
            + "<th>Right size</th><th>Right modified (UTC)</th><th>Differences</th><th>Error</th></tr></thead><tbody>");
        foreach (var item in rows)
        {
            writer.Write("<tr><td>" + Encode(item.RelativePath) + "</td>");
            writer.Write("<td><span class=\"pill " + item.Status + "\">" + Label(item.Status) + "</span></td>");
            writer.Write("<td class=\"n\">" + Size(item.Left) + "</td><td>" + Encode(Modified(item.Left)) + "</td>");
            writer.Write("<td class=\"n\">" + Size(item.Right) + "</td><td>" + Encode(Modified(item.Right)) + "</td>");
            writer.Write("<td class=\"n\">" + Differences(item) + "</td><td>" + Encode(item.Error ?? string.Empty) + "</td></tr>");
        }

        writer.Write("</tbody></table></body></html>");
    }

    /// <summary>Writes RFC 4180 CSV with invariant values, raw status names and ISO-8601 UTC timestamps.</summary>
    public static void WriteCsv(FolderCompareResult result, FolderReportOptions options, TextWriter writer)
    {
        Validate(result, options, writer);
        writer.Write("Path,Status,LeftSize,LeftModifiedUtc,RightSize,RightModifiedUtc,Differences,Error\r\n");
        foreach (var item in Rows(result, options))
        {
            var fields = new[]
            {
                item.RelativePath, item.Status.ToString(), Size(item.Left), Modified(item.Left), Size(item.Right),
                Modified(item.Right), Differences(item), item.Error ?? string.Empty,
            };
            writer.Write(string.Join(",", fields.Select(Quote)));
            writer.Write("\r\n");
        }
    }

    private static IReadOnlyList<FolderCompareItem> Rows(FolderCompareResult result, FolderReportOptions options) =>
        result.Items
            .Where(i => i.Status != FolderCompareStatus.Skipped
                && (options.IncludeIdentical || i.Status != FolderCompareStatus.Identical)
                && (i.Kind != FolderItemKind.Folder
                    || i.Status is FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly or FolderCompareStatus.TypeConflict or FolderCompareStatus.Error))
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RelativePath, StringComparer.Ordinal)
            .ToList();

    private static string Label(FolderCompareStatus status) => status switch
    {
        FolderCompareStatus.Identical => "Identical",
        FolderCompareStatus.Different => "Different",
        FolderCompareStatus.LeftOnly => "Left only",
        FolderCompareStatus.RightOnly => "Right only",
        FolderCompareStatus.TypeConflict => "File vs folder",
        FolderCompareStatus.Error => "Unreadable",
        _ => status.ToString(),
    };

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Size(FileMeta meta) => meta.Exists ? meta.Size.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Modified(FileMeta meta) =>
        meta.Exists ? DateTime.SpecifyKind(meta.LastWriteUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : string.Empty;

    private static string Differences(FolderCompareItem item) =>
        item.DifferenceCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Validate(FolderCompareResult result, FolderReportOptions options, TextWriter writer)
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
    }
}
