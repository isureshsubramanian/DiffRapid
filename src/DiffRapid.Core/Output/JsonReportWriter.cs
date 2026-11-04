using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>
/// Writes file and folder comparisons as indented UTF-8 JSON for scripts and CI. Property names are camelCase and
/// fixed; enum values are written as fixed lowerCamel strings, not taken from the C# names. A property is only
/// added in a later schema version, never renamed or removed without raising <see cref="SchemaVersion"/>.
/// </summary>
/// <remarks>
/// <para>Line numbers are one-based. For an empty range (count 0) the line number is where the lines would be
/// inserted, i.e. the number of the line that follows.</para>
/// <para>File comparison (<c>"kind": "file"</c>):</para>
/// <code>
/// {
///   "version": 1,
///   "kind": "file",
///   "left":  { "label": "a.txt", "lineCount": 10 },
///   "right": { "label": "b.txt", "lineCount": 11 },
///   "identical": false,
///   "differenceCount": 1,                       // important differences only
///   "lines": { "changed": 1, "added": 1, "removed": 0 },
///   "hunks": [                                   // every difference in document order
///     { "kind": "changed",                       // changed | added | removed
///       "ignored": false,                        // true when hidden by an ignore rule or line filter
///       "leftLine": 3, "leftCount": 1, "rightLine": 3, "rightCount": 2,
///       "left": ["old text"], "right": ["new text", "more"] }
///   ],
///   "moves": [ { "leftLine": 7, "rightLine": 2, "lineCount": 3 } ]
/// }
/// </code>
/// <para>Folder comparison (<c>"kind": "folder"</c>):</para>
/// <code>
/// {
///   "version": 1,
///   "kind": "folder",
///   "leftRoot": "C:/src/a",
///   "rightRoot": "C:/src/b",
///   "status": "different",                      // status of the whole comparison
///   "error": null,
///   "counts": { "identical": 3, "different": 1, "leftOnly": 0, "rightOnly": 2,
///               "typeConflict": 0, "skipped": 0, "error": 0 },
///   "items": [                                   // every entry, folders before their contents
///     { "path": "src/a.cs",                      // relative, '/' separated
///       "type": "file",                          // file | folder | link
///       "status": "different",                   // identical | different | leftOnly | rightOnly |
///                                                // typeConflict | skipped | error
///       "differenceCount": 2,                    // null when not compared as text
///       "error": null,
///       "left":  { "size": 120, "modifiedUtc": "2026-01-02T03:04:05.0000000Z" },  // null when absent
///       "right": { "size": 98,  "modifiedUtc": "2026-01-02T03:04:06.0000000Z" } }
///   ]
/// }
/// </code>
/// </remarks>
public static class JsonReportWriter
{
    /// <summary>The value of the <c>version</c> property written by this release.</summary>
    public const int SchemaVersion = 1;

    // Output is flushed to the stream whenever this much is buffered, so memory stays bounded on large inputs.
    private const int FlushThreshold = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>Writes a text comparison; labels come from the options, other report options are ignored.</summary>
    public static void WriteFile(
        TextDiffResult result,
        FileReportOptions options,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        Validate(result, options, stream);
        using var json = new Utf8JsonWriter(stream, WriterOptions);
        json.WriteStartObject();
        json.WriteNumber("version", SchemaVersion);
        json.WriteString("kind", "file");
        WriteDocument(json, "left", options.LeftLabel, result.Left);
        WriteDocument(json, "right", options.RightLabel, result.Right);
        json.WriteBoolean("identical", result.AreEqual);
        json.WriteNumber("differenceCount", result.DifferenceCount);
        var stats = DiffLineStats.From(result);
        json.WriteStartObject("lines");
        json.WriteNumber("changed", stats.Changed);
        json.WriteNumber("added", stats.Added);
        json.WriteNumber("removed", stats.Removed);
        json.WriteEndObject();

        json.WriteStartArray("hunks");
        foreach (var hunk in result.Hunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteHunk(json, result, hunk);
            FlushIfLarge(json);
        }

        json.WriteEndArray();
        json.WriteStartArray("moves");
        foreach (var move in result.MovedBlocks)
        {
            json.WriteStartObject();
            json.WriteNumber("leftLine", move.LeftStart + 1);
            json.WriteNumber("rightLine", move.RightStart + 1);
            json.WriteNumber("lineCount", move.LineCount);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    /// <summary>Writes a folder comparison: the overall status, the counts and every item.</summary>
    public static void WriteFolder(FolderCompareResult result, Stream stream, CancellationToken cancellationToken = default)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var json = new Utf8JsonWriter(stream, WriterOptions);
        json.WriteStartObject();
        json.WriteNumber("version", SchemaVersion);
        json.WriteString("kind", "folder");
        json.WriteString("leftRoot", Slashes(result.LeftRoot));
        json.WriteString("rightRoot", Slashes(result.RightRoot));
        json.WriteString("status", StatusName(result.Root.Status));
        WriteNullableString(json, "error", result.Root.Error);
        json.WriteStartObject("counts");
        foreach (FolderCompareStatus status in Enum.GetValues(typeof(FolderCompareStatus)))
        {
            json.WriteNumber(StatusName(status), result.Counts.TryGetValue(status, out var count) ? count : 0);
        }

        json.WriteEndObject();
        json.WriteStartArray("items");
        foreach (var item in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteItem(json, item);
            FlushIfLarge(json);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    internal static string StatusName(FolderCompareStatus status) => status switch
    {
        FolderCompareStatus.Identical => "identical",
        FolderCompareStatus.Different => "different",
        FolderCompareStatus.LeftOnly => "leftOnly",
        FolderCompareStatus.RightOnly => "rightOnly",
        FolderCompareStatus.TypeConflict => "typeConflict",
        FolderCompareStatus.Skipped => "skipped",
        FolderCompareStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string KindName(DiffKind kind) => kind switch
    {
        DiffKind.Changed => "changed",
        DiffKind.Added => "added",
        DiffKind.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TypeName(FolderItemKind kind) => kind switch
    {
        FolderItemKind.File => "file",
        FolderItemKind.Folder => "folder",
        FolderItemKind.Link => "link",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void WriteDocument(Utf8JsonWriter json, string name, string label, TextDocument document)
    {
        json.WriteStartObject(name);
        json.WriteString("label", label);
        json.WriteNumber("lineCount", document.LineCount);
        json.WriteEndObject();
    }

    private static void WriteHunk(Utf8JsonWriter json, TextDiffResult result, DiffHunk hunk)
    {
        json.WriteStartObject();
        json.WriteString("kind", KindName(hunk.Kind));
        json.WriteBoolean("ignored", hunk.Significance == DiffSignificance.Ignored);
        json.WriteNumber("leftLine", hunk.LeftStart + 1);
        json.WriteNumber("leftCount", hunk.LeftCount);
        json.WriteNumber("rightLine", hunk.RightStart + 1);
        json.WriteNumber("rightCount", hunk.RightCount);
        WriteLines(json, "left", result.Left, hunk.LeftStart, hunk.LeftCount);
        WriteLines(json, "right", result.Right, hunk.RightStart, hunk.RightCount);
        json.WriteEndObject();
    }

    private static void WriteLines(Utf8JsonWriter json, string name, TextDocument document, int start, int count)
    {
        json.WriteStartArray(name);
        for (var i = start; i < start + count; i++)
        {
            json.WriteStringValue(document[i].Content);
        }

        json.WriteEndArray();
    }

    private static void WriteItem(Utf8JsonWriter json, FolderCompareItem item)
    {
        json.WriteStartObject();
        json.WriteString("path", Slashes(item.RelativePath));
        json.WriteString("type", TypeName(item.Kind));
        json.WriteString("status", StatusName(item.Status));
        if (item.DifferenceCount is { } differences)
        {
            json.WriteNumber("differenceCount", differences);
        }
        else
        {
            json.WriteNull("differenceCount");
        }

        WriteNullableString(json, "error", item.Error);
        WriteMeta(json, "left", item.Left);
        WriteMeta(json, "right", item.Right);
        json.WriteEndObject();
    }

    private static void WriteMeta(Utf8JsonWriter json, string name, FileMeta meta)
    {
        if (!meta.Exists)
        {
            json.WriteNull(name);
            return;
        }

        json.WriteStartObject(name);
        json.WriteNumber("size", meta.Size);
        json.WriteString("modifiedUtc", DateTime.SpecifyKind(meta.LastWriteUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
        json.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static void FlushIfLarge(Utf8JsonWriter json)
    {
        if (json.BytesPending > FlushThreshold)
        {
            json.Flush();
        }
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static void Validate(TextDiffResult result, FileReportOptions options, Stream stream)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
    }
}
