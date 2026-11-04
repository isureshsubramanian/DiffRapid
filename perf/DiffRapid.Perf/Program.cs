using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace DiffRapid.Perf;

internal static class Program
{
    private static readonly string[] Scenarios = { "T1", "T2", "F1", "Full" };

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 3 && arguments[0] == "--worker")
        {
            Console.WriteLine(JsonSerializer.Serialize(Measurement.Run(arguments[1], arguments[2])));
            return 0;
        }

        if (arguments.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("DiffRapid.Perf [--runs <positive integer>] [--data <directory>] [--scenario T1|T2|F1|Full]");
            return 0;
        }

        return Run(arguments);
    }

    private static (int Runs, string? Directory, string? Scenario) ParseArguments(string[] arguments)
    {
        var runs = 3;
        string? directory = null;
        string? selected = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (index + 1 >= arguments.Length)
            {
                throw new ArgumentException("Each option requires a value.");
            }

            var option = arguments[index];
            var value = arguments[++index];
            switch (option)
            {
                case "--runs" when int.TryParse(value, out runs) && runs > 0:
                    break;
                case "--data":
                    directory = Path.GetFullPath(value);
                    break;
                case "--scenario" when Scenarios.Contains(value, StringComparer.Ordinal):
                    selected = value;
                    break;
                default:
                    throw new ArgumentException("Invalid option or value: " + option);
            }
        }

        return (runs, directory, selected);
    }

    private static int Run(string[] arguments)
    {
        var (runs, directory, selected) = ParseArguments(arguments);
        var temporary = directory is null;
        directory ??= Path.Combine(Path.GetTempPath(), "diffrapid-perf-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            Console.WriteLine($"Data: {directory}; runs: {runs}; runtime: {Environment.Version}; processors: {Environment.ProcessorCount}; architecture: {RuntimeInformation.ProcessArchitecture}; OS: {RuntimeInformation.OSDescription}");
            var root = DataSet.Prepare(directory, selected is null or "F1" or "Full", cancellation.Token);
            Console.WriteLine("Scenario | median ms | allocated bytes (max) | managed peak (max) | working set peak (max) | budget | result");
            var passed = true;
            foreach (var scenario in selected is null ? Scenarios : new[] { selected })
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var measurements = new List<Measurement>();
                for (var run = 0; run < runs; run++)
                {
                    Console.WriteLine($"Running {scenario} ({run + 1}/{runs})...");
                    measurements.Add(RunWorker(scenario, root, cancellation.Token));
                }

                passed &= PrintResult(scenario, measurements);
            }

            return passed ? 0 : 1;
        }
        finally
        {
            if (temporary && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static Measurement RunWorker(string scenario, string root, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start measurement process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(scenario == "T2" ? 15 : 660));
        using var registration = timeout.Token.Register(() =>
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        });
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            if (timeout.IsCancellationRequested)
            {
                return new Measurement(scenario == "T2" ? 15000 : 660000, 0, 0, 0, false);
            }

            throw new InvalidOperationException("Measurement process failed: " + output);
        }

        return JsonSerializer.Deserialize<Measurement>(output) ?? throw new InvalidOperationException("Missing measurement.");
    }

    private static bool PrintResult(string scenario, List<Measurement> measurements)
    {
        var times = measurements.Select(value => value.Milliseconds).OrderBy(value => value).ToArray();
        var middle = times.Length / 2;
        var median = times.Length % 2 == 0 ? (times[middle - 1] + times[middle]) / 2 : times[middle];
        var allocated = measurements.Max(value => value.AllocatedBytes);
        var managed = measurements.Max(value => value.PeakManagedBytes);
        var workingSet = measurements.Max(value => value.PeakWorkingSet);
        var completed = measurements.All(value => value.Completed);
        var passed = completed && scenario switch
        {
            "T1" => median < 2000 && allocated < 400_000_000,
            "T2" => median < 10000,
            "F1" => median < 15000 && managed < 1_000_000_000 && allocated < 1_000_000_000,
            _ => true,
        };
        var budget = scenario switch
        {
            "T1" => "<2000 ms; <400 MB total allocation",
            "T2" => "<10000 ms; bounded fallback",
            "F1" => "<15000 ms; <1 GB managed peak",
            _ => "time reported only",
        };
        Console.WriteLine($"{scenario} | {median:F1} | {allocated} | {managed} | {workingSet} | {budget} | {(passed ? "PASS" : "FAIL")}{(completed ? "" : " (timed out)")}");
        return passed;
    }
}
