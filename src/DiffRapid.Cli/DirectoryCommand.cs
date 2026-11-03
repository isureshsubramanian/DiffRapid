using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffRapid.Core.Folders;

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

        var result = FolderComparer.Compare(args.Positionals[0], args.Positionals[1], new FolderCompareOptions
        {
            Method = method,
            Recursive = !args.Has("no-recurse"),
            IncludeFiles = args.Values("include"),
            ExcludeFiles = args.Values("exclude"),
            TextOptions = args.ToCompareOptions(),
        });
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
        return result.Root.Status == FolderCompareStatus.Error ? ExitError :
            result.Root.Status is FolderCompareStatus.Identical or FolderCompareStatus.Skipped ? ExitSame : ExitDifferent;
    }
}
