using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;

namespace DiffRapid.VisualStudio.Options;

/// <summary>
/// Tools > Options > DiffRapid: the settings new comparisons start with, registered in
/// UnifiedSettings\registration.json. The toolbar of a comparison still changes them for that comparison only.
/// </summary>
internal sealed class DiffRapidSettings
{
    private const string FilePrefix = "diffRapid.fileComparison.";
    private const string FolderPrefix = "diffRapid.folderComparison.";

    private DiffRapidSettings(CompareOptions compare, FolderCompareOptions folder)
    {
        Compare = compare;
        Folder = folder;
    }

    /// <summary>Text comparison settings for a new comparison.</summary>
    public CompareOptions Compare { get; }

    /// <summary>Folder comparison settings for a new comparison; files are compared with <see cref="Compare"/>.</summary>
    public FolderCompareOptions Folder { get; }

    /// <summary>Reads the current settings; anything unreadable falls back to its registered default.</summary>
    public static async Task<DiffRapidSettings> ReadAsync(IAsyncServiceProvider services)
    {
        var manager = await services.GetServiceAsync(typeof(SVsUnifiedSettingsManager)) as ISettingsManager;
        var reader = manager?.GetReader();

        var compare = CompareOptions.Default with
        {
            Whitespace = Enum(reader, FilePrefix + "whitespace", WhitespaceMode.Compare),
            IgnoreCase = Value(reader, FilePrefix + "ignoreCase", false),
            IgnoreBlankLines = Value(reader, FilePrefix + "ignoreBlankLines", false),
            IgnoreLineEndings = Value(reader, FilePrefix + "ignoreLineEndings", false),
            IgnoreNumbers = Value(reader, FilePrefix + "ignoreNumbers", false),
            IgnoreMissingTrailingLineEnding = Value(reader, FilePrefix + "ignoreMissingFinalLineEnding", false),
            DetectMovedBlocks = Value(reader, FilePrefix + "detectMovedBlocks", true),
            Algorithm = Enum(reader, FilePrefix + "algorithm", DiffAlgorithmKind.Histogram),
            InlineGranularity = Enum(reader, FilePrefix + "inlineGranularity", InlineGranularity.Word),
            LineFilters = List(reader, FilePrefix + "lineFilters").Select(p => new LineFilter(p)).ToList(),
        };

        var folder = new FolderCompareOptions
        {
            Method = Enum(reader, FolderPrefix + "compareBy", FolderCompareMethod.FullContents),
            Recursive = Value(reader, FolderPrefix + "includeSubfolders", true),
            DefaultExcludes = Value(reader, FolderPrefix + "skipCommonFolders", true),
            CaseSensitive = Value(reader, FolderPrefix + "caseSensitiveNames", false),
            TimeTolerance = TimeSpan.FromSeconds(Math.Max(0, Value(reader, FolderPrefix + "timeToleranceSeconds", 2))),
            IncludeFiles = List(reader, FolderPrefix + "includeFiles").Select(m => m.Trim()).ToArray(),
            ExcludeFiles = List(reader, FolderPrefix + "excludeFiles").Select(m => m.Trim()).ToArray(),
            ExcludeFolders = List(reader, FolderPrefix + "excludeFolders").Select(m => m.Trim()).ToArray(),
            TextOptions = compare,
        };

        return new DiffRapidSettings(compare, folder);
    }

    private static T Value<T>(ISettingsReader? reader, string moniker, T fallback)
        where T : notnull
    {
        if (reader is null)
        {
            return fallback;
        }

        var read = reader.GetValue<T>(moniker, SettingReadOptions.RequireValidation);
        return read.Outcome == SettingRetrievalOutcome.Success && read.Value is not null ? read.Value : fallback;
    }

    // Enum settings are stored as the camel-cased member name ("ignoreChange" for IgnoreChange).
    private static TEnum Enum<TEnum>(ISettingsReader? reader, string moniker, TEnum fallback)
        where TEnum : struct =>
        System.Enum.TryParse<TEnum>(Value(reader, moniker, string.Empty), ignoreCase: true, out var value) ? value : fallback;

    // Blank entries are dropped; patterns are not trimmed, since spaces can be part of a regular expression.
    private static IEnumerable<string> List(ISettingsReader? reader, string moniker)
    {
        if (reader is null)
        {
            return Array.Empty<string>();
        }

        var read = reader.GetArray<ListEntry>(moniker, SettingReadOptions.RequireValidation);
        return read.Outcome == SettingRetrievalOutcome.Success && read.Value is not null
            ? read.Value.Select(e => e?.Pattern ?? e?.Mask).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
            : Array.Empty<string>();
    }

    // One row of a list setting. The settings page only edits lists of objects, so each row is an object
    // with a single "pattern" (line filters) or "mask" (folder lists) property.
    private sealed class ListEntry
    {
        public string? Pattern { get; set; }

        public string? Mask { get; set; }
    }

    // The service identifier lives in an internal interop assembly that is not referenceable; the
    // service is looked up by GUID, so a local marker type with the same GUID resolves to it.
    [Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
    private interface SVsUnifiedSettingsManager
    {
    }
}
