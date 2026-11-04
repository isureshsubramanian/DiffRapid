using System;
using System.IO;
using System.Linq;
using System.Text;
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

        var method = args.Value("method")?.ToLowerInvariant() switch
        {
            null or "full" => FolderCompareMethod.FullContents,
            "quick" => FolderCompareMethod.QuickContents,
            "binary" => FolderCompareMethod.BinaryContents,
            "date" => FolderCompareMethod.Date,
            "date-size" => FolderCompareMethod.DateAndSize,
            "size" => FolderCompareMethod.Size,
            "existence" => FolderCompareMethod.Existence,
            var other => throw new UsageException($"Unknown method '{other}'."),
        };
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

        var result = FolderComparer.Compare(args.Positionals[0], args.Positionals[1], new FolderCompareOptions
        {
            Method = method,
            Recursive = !args.Has("no-recurse"),
            IncludeFiles = args.Values("include"),
            ExcludeFiles = args.Values("exclude"),
            Filter = args.Value("filter-file") is { } filterPath
                ? FolderFilter.Parse(TextFileReader.ReadFile(filterPath).Text) : null,
            TextOptions = args.ToCompareOptions(),
        });
        if (args.Has("json"))
        {
            WriteFolderJson(result);
        }
        else
        {
            foreach (var item in result.Items)
            {
                if (item.Status is FolderCompareStatus.Identical or FolderCompareStatus.Skipped)
                {
                    continue;
                }

                if (only == "left" && item.Status != FolderCompareStatus.LeftOnly ||
                    only == "right" && item.Status != FolderCompareStatus.RightOnly ||
                    only == "different" && item.Status is not (FolderCompareStatus.Different or FolderCompareStatus.TypeConflict or FolderCompareStatus.Error))
                {
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
                WriteFolderHtml(args, result, options, writer);
            }
        }

        return result.Root.Status == FolderCompareStatus.Error ? ExitError :
            result.Root.Status is FolderCompareStatus.Identical or FolderCompareStatus.Skipped ? ExitSame : ExitDifferent;
    }
}
