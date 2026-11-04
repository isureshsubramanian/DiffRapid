using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Ai;
using DiffRapid.Core.Folders;
using DiffRapid.Core.History;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using Newtonsoft.Json.Linq;

namespace DiffRapid.VisualStudio.Options;

/// <summary>
/// Tools > Options > DiffRapid: the settings new comparisons start with, registered in
/// UnifiedSettings\registration.json. The toolbar of a comparison still changes them for that comparison only.
/// </summary>
internal sealed class DiffRapidSettings
{
    private const string FilePrefix = "diffRapid.fileComparison.";
    private const string FolderPrefix = "diffRapid.folderComparison.";
    private const string HistoryPrefix = "diffRapid.localHistory.";
    private const string AiPrefix = "diffRapid.ai.";

    private DiffRapidSettings(CompareOptions compare, FolderCompareOptions folder, bool keepHistory, LocalHistoryOptions history, AiEndpoint? ai, bool redactSecrets)
    {
        Compare = compare;
        Folder = folder;
        KeepHistory = keepHistory;
        History = history;
        Ai = ai;
        RedactSecrets = redactSecrets;
    }

    /// <summary>Whether saved files are kept in the local history.</summary>
    public bool KeepHistory { get; }

    /// <summary>How long and how many versions the local history keeps.</summary>
    public LocalHistoryOptions History { get; }

    /// <summary>The AI service the user set up, or null when no endpoint is set and the AI features are off.</summary>
    public AiEndpoint? Ai { get; }

    /// <summary>Whether passwords, keys and tokens are masked before text is sent to the AI service.</summary>
    public bool RedactSecrets { get; }

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

        var history = new LocalHistoryOptions
        {
            MaxAge = TimeSpan.FromDays(Math.Max(1, Value(reader, HistoryPrefix + "keepDays", 30))),
            MaxEntriesPerFile = Math.Max(1, Value(reader, HistoryPrefix + "versionsPerFile", 100)),
        };

        return new DiffRapidSettings(
            compare,
            folder,
            Value(reader, HistoryPrefix + "enabled", true),
            history,
            ReadAi(reader),
            Value(reader, AiPrefix + "redactSecrets", true));
    }

    /// <summary>The configured AI service; the key comes from the environment variable named in the settings, never from the settings.</summary>
    private static AiEndpoint? ReadAi(ISettingsReader? reader)
    {
        var endpoint = Value(reader, AiPrefix + "endpoint", string.Empty).Trim();
        if (endpoint.Length == 0)
        {
            return null;
        }

        var model = Value(reader, AiPrefix + "model", string.Empty).Trim();
        var kind = Enum(reader, AiPrefix + "kind", AiEndpointKind.OpenAi);
        var keyVariable = Value(reader, AiPrefix + "apiKeyVariable", "OPENAI_API_KEY").Trim();
        var key = keyVariable.Length == 0 ? null : Environment.GetEnvironmentVariable(keyVariable);
        return new AiEndpoint
        {
            BaseUrl = endpoint,
            Model = model,
            Kind = kind,
            Deployment = kind == AiEndpointKind.AzureOpenAi ? model : null,
            ApiVersion = kind == AiEndpointKind.AzureOpenAi ? Value(reader, AiPrefix + "apiVersion", "2024-10-21").Trim() : null,
            ApiKey = string.IsNullOrEmpty(key) ? null : key,
        };
    }

    private static T Value<T>(ISettingsReader? reader, string moniker, T fallback)
        where T : notnull
    {
        if (reader is null)
        {
            return fallback;
        }

        var read = reader.GetValue<T>(moniker, SettingReadOptions.RequireValidation);
        if (read.Outcome == SettingRetrievalOutcome.Success && read.Value is not null)
        {
            return read.Value;
        }

        Diagnostics.LogWarning($"Could not read the {moniker} setting ({read.Outcome}: {read.Message}); using {fallback}.");
        return fallback;
    }

    // Enum settings are stored as the camel-cased member name ("ignoreChange" for IgnoreChange).
    private static TEnum Enum<TEnum>(ISettingsReader? reader, string moniker, TEnum fallback)
        where TEnum : struct =>
        System.Enum.TryParse<TEnum>(Value(reader, moniker, string.Empty), ignoreCase: true, out var value) ? value : fallback;

    // The settings page only edits lists of objects, so each row of a list setting is an object with a
    // single "pattern" (line filters) or "mask" (folder lists) property. Blank rows are dropped; patterns
    // are not trimmed, since spaces can be part of a regular expression.
    private static IEnumerable<string> List(ISettingsReader? reader, string moniker)
    {
        if (reader is null)
        {
            return Array.Empty<string>();
        }

        var read = reader.GetArray<object>(moniker, SettingReadOptions.NoRequirements);
        if (read.Outcome != SettingRetrievalOutcome.Success || read.Value is null)
        {
            Diagnostics.LogWarning($"Could not read the {moniker} setting ({read.Outcome}: {read.Message}).");
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var row in read.Value)
        {
            var text = RowText(row);
            if (text is null)
            {
                Diagnostics.LogWarning($"Skipped a row of the {moniker} setting that has no pattern or mask: {row?.GetType().FullName} {row}");
            }
            else if (text.Trim().Length > 0)
            {
                values.Add(text);
            }
        }

        return values;
    }

    // Rows come back as JSON objects; which JSON library represents them is up to Visual Studio.
    private static string? RowText(object? row)
    {
        switch (row)
        {
            case null:
                return null;
            case string text:
                return text;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element:
                return element.TryGetProperty("pattern", out var pattern) ? pattern.GetString()
                    : element.TryGetProperty("mask", out var mask) ? mask.GetString()
                    : null;
            default:
                var token = row as JToken ?? JToken.FromObject(row);
                return token is JObject item ? (string?)(item["pattern"] ?? item["mask"]) : null;
        }
    }

    // The service identifier lives in an internal interop assembly that is not referenceable; the
    // service is looked up by GUID, so a local marker type with the same GUID resolves to it.
    [Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
    private interface SVsUnifiedSettingsManager
    {
    }
}
