using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Sync;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunSync(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("sync needs two directories.");
        }

        var mode = args.Value("profile")?.ToLowerInvariant() switch
        {
            null => throw new UsageException("sync needs --profile mirror-right|mirror-left|update|update-both."),
            "mirror-right" => SyncMode.MirrorLeftToRight,
            "mirror-left" => SyncMode.MirrorRightToLeft,
            "update" => SyncMode.Update,
            "update-both" => SyncMode.UpdateBoth,
            var other => throw new UsageException($"Unknown sync profile '{other}'."),
        };

        var comparison = FolderComparer.Compare(args.Positionals[0], args.Positionals[1], new FolderCompareOptions
        {
            IncludeFiles = args.Values("include"),
            ExcludeFiles = args.Values("exclude"),
            TextOptions = args.ToCompareOptions(),
        });
        if (comparison.Root.Status == FolderCompareStatus.Error)
        {
            throw new IOException(comparison.Root.Error);
        }

        var profile = new SyncProfile
        {
            Mode = mode,
            Filter = args.Value("filter-file") is { } filterPath ? FolderFilter.Parse(TextFileReader.ReadFile(filterPath).Text) : null,
        };
        var plan = SyncPlanner.Plan(comparison, profile);
        if (!args.Has("yes"))
        {
            WriteSyncPlan(plan);
            return plan.IsEmpty && !plan.HasConflicts ? ExitSame : ExitDifferent;
        }

        var results = SyncExecutor.Execute(plan, options: new SyncExecuteOptions { DryRun = args.Has("dry-run") });
        WriteSyncResults(results);
        if (results.Any(r => r.Status == SyncOperationStatus.Failed))
        {
            return ExitError;
        }

        return plan.HasConflicts || (args.Has("dry-run") && !plan.IsEmpty) ? ExitDifferent : ExitSame;
    }

    private static void WriteSyncPlan(SyncPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            Console.Out.WriteLine(DescribeSyncOperation(operation));
        }

        var counts = Enum.GetValues(typeof(SyncAction)).Cast<SyncAction>().Select(action => $"{action}: {plan.Count(action)}");
        Console.Out.WriteLine(string.Join(", ", counts));
        if (!plan.IsEmpty)
        {
            Console.Out.WriteLine("Plan only; run again with --yes to synchronize.");
        }
    }

    private static void WriteSyncResults(IReadOnlyList<SyncOperationResult> results)
    {
        foreach (var result in results)
        {
            var error = result.Status == SyncOperationStatus.Failed ? $": {result.Error}" : "";
            Console.Out.WriteLine($"{result.Status,-11} {DescribeSyncOperation(result.Operation)}{error}");
        }

        var counts = Enum.GetValues(typeof(SyncOperationStatus)).Cast<SyncOperationStatus>()
            .Select(status => $"{status}: {results.Count(r => r.Status == status)}");
        Console.Out.WriteLine(string.Join(", ", counts));
    }

    private static string DescribeSyncOperation(SyncOperation operation)
    {
        var arrow = operation.Direction switch
        {
            SyncDirection.LeftToRight => "->",
            SyncDirection.RightToLeft => "<-",
            _ => "  ",
        };
        var reason = operation.Reason is null ? "" : $"  ({operation.Reason})";
        return $"{operation.Action,-12} {arrow}  {operation.RelativePath}{reason}";
    }
}
