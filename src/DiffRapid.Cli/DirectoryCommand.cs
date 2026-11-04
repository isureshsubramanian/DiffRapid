using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunDirectory(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("dir needs two directories.");
        }

        var method = FolderMethod(args);
        var only = args.Value("only")?.ToLowerInvariant() ?? "all";
        if (only is not ("all" or "different" or "left" or "right"))
        {
            throw new UsageException($"Unknown --only value '{only}'.");
        }

        var reportPath = args.Value("report");
        var reportFormat = args.Value("report-format")?.ToLowerInvariant();
        if (reportPath is null && args.Has("report-format"))
        {
            throw new UsageException("--report-format requires --report <file>.");
        }

        if (reportPath is not null)
        {
            reportFormat ??= Path.GetExtension(reportPath).ToLowerInvariant() switch
            {
                ".html" or ".htm" => "html",
                ".csv" => "csv",
                _ => throw new UsageException("Use .html, .htm or .csv, or specify --report-format html|csv."),
            };
            if (reportFormat is not ("html" or "csv"))
            {
                throw new UsageException($"Unknown report format '{reportFormat}'.");
            }
        }

        var compareOptions = FolderOptions(args, method) with
        {
            DetectRenames = args.Has("renames") || args.Has("similarity"),
            RenameSimilarityThreshold = Similarity(args),
        };
        var result = FolderComparer.Compare(args.Positionals[0], args.Positionals[1], compareOptions);
        if (args.Has("json"))
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            Console.Out.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        }
        else
        {
            foreach (var item in result.Items)
            {
                if (item.Status is FolderCompareStatus.Identical or FolderCompareStatus.Skipped)
                {
                    continue;
                }

                var isRename = item.Status is FolderCompareStatus.Renamed or FolderCompareStatus.Moved;
                if (only == "left" && item.Status != FolderCompareStatus.LeftOnly ||
                    only == "right" && item.Status != FolderCompareStatus.RightOnly ||
                    only == "different" && !isRename && item.Status is not (FolderCompareStatus.Different or FolderCompareStatus.TypeConflict or FolderCompareStatus.Error))
                {
                    continue;
                }

                // Both files of a rename pair are listed; print the pair once, from its left file.
                if (isRename)
                {
                    if (item.Left.Exists)
                    {
                        Console.Out.WriteLine(RenameLine(item));
                    }

                    continue;
                }

                var marker = item.Status switch
                {
                    FolderCompareStatus.LeftOnly => "<",
                    FolderCompareStatus.RightOnly => ">",
                    FolderCompareStatus.Different => "M",
                    _ => "!",
                };
                Console.Out.WriteLine($"{marker}  {item.RelativePath}" + (item.Error is null ? "" : $": {item.Error}"));
            }

            if (result.Root.Error is not null)
            {
                Console.Error.WriteLine(result.Root.Error);
            }

            Console.Out.WriteLine(string.Join(", ", result.Counts.Select(c => $"{c.Key}: {c.Value}")));
        }

        if (reportPath is not null)
        {
            using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(reportFormat == "csv"));
            var options = new FolderReportOptions
            {
                LeftRoot = args.Positionals[0],
                RightRoot = args.Positionals[1],
                GeneratedAt = DateTimeOffset.UtcNow,
                MethodLabel = method.ToString(),
                IncludeIdentical = args.Has("include-identical"),
            };
            if (reportFormat == "csv")
            {
                FolderReportWriter.WriteCsv(result, options, writer);
            }
            else
            {
                FolderReportWriter.WriteHtml(result, options, writer);
            }
        }

        return result.Root.Status == FolderCompareStatus.Error ? ExitError :
            result.Root.Status is FolderCompareStatus.Identical or FolderCompareStatus.Skipped ? ExitSame : ExitDifferent;
    }

    private static FolderCompareMethod FolderMethod(ParsedArgs args) =>
        args.Value("method")?.ToLowerInvariant() switch
        {
            null or "full" => FolderCompareMethod.FullContents,
            "quick" => FolderCompareMethod.QuickContents,
            "binary" => FolderCompareMethod.BinaryContents,
            "date" => FolderCompareMethod.Date,
            "date-size" => FolderCompareMethod.DateAndSize,
            "size" => FolderCompareMethod.Size,
            "existence" => FolderCompareMethod.Existence,
            "hash" => FolderCompareMethod.Hash,
            var other => throw new UsageException($"Unknown method '{other}'."),
        };

    /// <summary>The folder options shared by dir and dir3.</summary>
    private static FolderCompareOptions FolderOptions(ParsedArgs args, FolderCompareMethod method) => new()
    {
        Method = method,
        Recursive = !args.Has("no-recurse"),
        IncludeFiles = args.Values("include"),
        ExcludeFiles = args.Values("exclude"),
        Filter = args.Value("filter-file") is { } filterPath
            ? FolderFilter.Parse(TextFileReader.ReadFile(filterPath).Text) : null,
        TextOptions = args.ToCompareOptions(),
    };

    private static double? Similarity(ParsedArgs args)
    {
        if (args.Value("similarity") is not { } text)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var similarity)
            || double.IsNaN(similarity)
            || similarity <= 0
            || similarity > 1)
        {
            throw new UsageException("--similarity needs a number above 0 and at most 1, such as 0.6.");
        }

        return similarity;
    }

    private static string RenameLine(FolderCompareItem item)
    {
        var line = $"R  {item.RelativePath} -> {item.CounterpartPath}";
        if (item.Similarity is < 1 and { } similarity)
        {
            line += string.Format(CultureInfo.InvariantCulture, " ({0:0}% similar)", similarity * 100);
        }

        return line;
    }
}
