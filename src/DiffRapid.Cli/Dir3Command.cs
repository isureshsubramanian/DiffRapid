using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffRapid.Core.Folders;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunDirectory3(ParsedArgs args)
    {
        if (args.Positionals.Count != 3)
        {
            throw new UsageException("dir3 needs three directories: base, left and right.");
        }

        var result = ThreeWayFolderComparer.Compare(
            args.Positionals[0],
            args.Positionals[1],
            args.Positionals[2],
            FolderOptions(args, FolderMethod(args)));
        if (args.Has("json"))
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            Console.Out.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        }
        else
        {
            WriteThreeWayItems(result);
        }

        if (result.Root.Status == ThreeWayFolderStatus.Error)
        {
            return ExitError;
        }

        // Like merge: 0 when the changes combine cleanly, 1 when something conflicts or could not be read.
        var unresolved = result.Counts[ThreeWayFolderStatus.Conflict] + result.Counts[ThreeWayFolderStatus.Error];
        return unresolved == 0 ? ExitSame : ExitDifferent;
    }

    private static void WriteThreeWayItems(ThreeWayFolderResult result)
    {
        foreach (var item in result.Items)
        {
            // A folder that exists everywhere is summarised by the entries inside it.
            var isAggregate = item.Kind == FolderItemKind.Folder && item.Base.Exists && item.Left.Exists && item.Right.Exists;
            if (item.Status is ThreeWayFolderStatus.Unchanged or ThreeWayFolderStatus.Skipped || isAggregate)
            {
                continue;
            }

            var label = item.Status switch
            {
                ThreeWayFolderStatus.ChangedLeft => "changed left",
                ThreeWayFolderStatus.ChangedRight => "changed right",
                ThreeWayFolderStatus.ChangedBothIdentically => "changed both (same)",
                ThreeWayFolderStatus.ChangedBoth => "changed both",
                ThreeWayFolderStatus.Conflict => "conflict",
                ThreeWayFolderStatus.AddedLeft => "added left",
                ThreeWayFolderStatus.AddedRight => "added right",
                ThreeWayFolderStatus.AddedBothIdentically => "added both (same)",
                ThreeWayFolderStatus.RemovedLeft => "removed left",
                ThreeWayFolderStatus.RemovedRight => "removed right",
                ThreeWayFolderStatus.RemovedBoth => "removed both",
                _ => "error",
            };
            Console.Out.WriteLine($"{label,-20} {item.RelativePath}" + (item.Error is null ? "" : $": {item.Error}"));
        }

        if (result.Root.Error is not null)
        {
            Console.Error.WriteLine(result.Root.Error);
        }

        Console.Out.WriteLine(string.Join(", ", result.Counts.Where(c => c.Value != 0).Select(c => $"{c.Key}: {c.Value}")));
    }
}
