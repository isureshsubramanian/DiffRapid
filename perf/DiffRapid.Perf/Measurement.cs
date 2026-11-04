using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Text;

namespace DiffRapid.Perf;

internal sealed record Measurement(double Milliseconds, long AllocatedBytes, long PeakManagedBytes, long PeakWorkingSet, bool Completed)
{
    public static Measurement Run(string scenario, string root)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long peakManaged = GC.GetTotalMemory(false);
        using var sampler = new Timer(_ => UpdatePeak(ref peakManaged, GC.GetTotalMemory(false)), null, 0, 10);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(scenario == "T2" ? 10 : 600));
        var allocated = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        var completed = true;
        try
        {
            RunScenario(scenario, root, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            completed = false;
        }

        stopwatch.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        UpdatePeak(ref peakManaged, GC.GetTotalMemory(false));
        using var process = Process.GetCurrentProcess();
        return new Measurement(stopwatch.Elapsed.TotalMilliseconds, allocated, peakManaged, process.PeakWorkingSet64, completed);
    }

    private static void RunScenario(string scenario, string root, CancellationToken cancellationToken)
    {
        if (scenario is "T1" or "T2")
        {
            var left = TextDocument.Parse(File.ReadAllText(Path.Combine(root, "left.txt")));
            var rightName = scenario == "T1" ? "scattered.txt" : "different.txt";
            var right = TextDocument.Parse(File.ReadAllText(Path.Combine(root, rightName)));
            var result = TextComparer.Compare(left, right, cancellationToken: cancellationToken);
            var expected = scenario == "T1" ? 4000 : DataSet.LineCount;
            if (result.Hunks.Sum(hunk => hunk.LeftCount) != expected || result.Hunks.Sum(hunk => hunk.RightCount) != expected)
            {
                throw new InvalidOperationException("Unexpected text comparison result.");
            }

            GC.KeepAlive(result);
            return;
        }

        var options = new FolderCompareOptions
        {
            Method = scenario == "F1" ? FolderCompareMethod.DateAndSize : FolderCompareMethod.FullContents,
        };
        var folderResult = FolderComparer.Compare(Path.Combine(root, "tree-left"), Path.Combine(root, "tree-right"), options, cancellationToken: cancellationToken);
        var changedFiles = folderResult.Items.Count(item => item.Kind == FolderItemKind.File && item.Status == FolderCompareStatus.Different);
        if (folderResult.Items.Count != DataSet.FolderCount * (DataSet.FilesPerFolder + 1)
            || changedFiles != 2000 || folderResult.Counts[FolderCompareStatus.Error] != 0)
        {
            throw new InvalidOperationException("Unexpected folder comparison result.");
        }

        GC.KeepAlive(folderResult);
    }

    private static void UpdatePeak(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
