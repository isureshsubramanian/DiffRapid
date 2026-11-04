using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.History;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunHistory(string[] rawArgs)
    {
        var args = ParsedArgs.Parse(JoinHistoryOptionValues(rawArgs), 1);
        ValidateHistoryArguments(args);
        var storePath = args.Value("store") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffRapid", "History");
        var store = new LocalHistoryStore(storePath, new LocalHistoryOptions());
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return RunHistoryAction(args, store, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return Fail("History operation canceled.");
        }
        catch (InvalidDataException exception)
        {
            return Fail(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int RunHistoryAction(ParsedArgs args, LocalHistoryStore store, CancellationToken cancellationToken)
    {
        var action = args.Positionals[0].ToLowerInvariant();
        if (action == "prune")
        {
            store.Prune(cancellationToken);
            return ExitSame;
        }

        var path = args.Positionals[1];
        if (action == "snapshot")
        {
            var entry = store.Snapshot(path, args.Value("label") ?? "Saved", cancellationToken: cancellationToken);
            Console.Out.WriteLine(entry is null ? "Skipped (unchanged, oversized or binary)." : $"{entry.TimestampUtc:O} {entry.Hash} {entry.Label}");
            return ExitSame;
        }

        var entries = store.List(path, cancellationToken);
        if (action == "list")
        {
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                var note = entry.Note is null ? "" : " " + entry.Note;
                Console.Out.WriteLine($"{index + 1} {entry.TimestampUtc:O} {entry.Size} {entry.Hash} {entry.Label}{note}");
            }

            return ExitSame;
        }

        var left = HistoryEntryAt(entries, args.Positionals[2]);
        if (action == "show")
        {
            var bytes = store.Read(left, cancellationToken);
            using var output = Console.OpenStandardOutput();
            output.Write(bytes, 0, bytes.Length);
            return ExitSame;
        }

        return WriteHistoryDiff(args, store, entries, left, path, cancellationToken);
    }

    private static int WriteHistoryDiff(ParsedArgs args, LocalHistoryStore store, IReadOnlyList<LocalHistoryEntry> entries,
        LocalHistoryEntry leftEntry, string path, CancellationToken cancellationToken)
    {
        var encoding = args.Has("cp") ? Encoding.GetEncoding(args.IntValue("cp", 0)) : null;
        var left = LoadHistoryText(store.Read(leftEntry, cancellationToken), encoding);
        var right = args.Positionals.Count == 4
            ? LoadHistoryText(store.Read(HistoryEntryAt(entries, args.Positionals[3]), cancellationToken), encoding)
            : Load(path, encoding);
        var result = TextComparer.Compare(left, right, args.ToCompareOptions(), cancellationToken);
        var leftLabel = path + "@" + args.Positionals[2];
        var rightLabel = args.Positionals.Count == 4 ? path + "@" + args.Positionals[3] : path;
        if (!args.Has("quiet"))
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            UnifiedDiffWriter.Write(output, result, args.Value("left-label") ?? leftLabel,
                args.Value("right-label") ?? rightLabel, args.IntValue("context", 3));
        }

        return result.AreEqual ? ExitSame : ExitDifferent;
    }

    private static TextDocument LoadHistoryText(byte[] bytes, Encoding? encoding)
    {
        var decoded = TextFileReader.Decode(bytes, overrideEncoding: encoding);
        if (decoded.IsBinary)
        {
            throw new UsageException("The snapshot looks like a binary file and cannot be compared as text.");
        }

        return TextDocument.Parse(decoded.Text);
    }

    private static LocalHistoryEntry HistoryEntryAt(IReadOnlyList<LocalHistoryEntry> entries, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > entries.Count)
        {
            throw new UsageException($"Snapshot '{value}' does not exist. Number snapshots from 1 (newest).");
        }

        return entries[number - 1];
    }

    private static void ValidateHistoryArguments(ParsedArgs args)
    {
        if (args.Positionals.Count == 0)
        {
            throw new UsageException("history needs list, show, diff, snapshot or prune.");
        }

        var count = args.Positionals.Count;
        var valid = args.Positionals[0].ToLowerInvariant() switch
        {
            "list" or "snapshot" => count == 2,
            "show" => count == 3,
            "diff" => count is 3 or 4,
            "prune" => count == 1,
            _ => false,
        };
        if (!valid)
        {
            throw new UsageException("Use history list <file>, show <file> <n>, diff <file> <n> [<m>], snapshot <file> or prune.");
        }

        foreach (var option in new[] { "store", "label" })
        {
            if (args.Has(option) && string.IsNullOrWhiteSpace(args.Value(option)))
            {
                throw new UsageException($"--{option} needs a nonempty value.");
            }
        }
    }

    private static string[] JoinHistoryOptionValues(string[] rawArgs)
    {
        var joined = new List<string>();
        for (var index = 0; index < rawArgs.Length; index++)
        {
            var argument = rawArgs[index];
            if (argument.Equals("--store", StringComparison.OrdinalIgnoreCase) || argument.Equals("--label", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= rawArgs.Length || rawArgs[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"Option '{argument}' needs a value.");
                }

                argument += "=" + rawArgs[++index];
            }

            joined.Add(argument);
        }

        return joined.ToArray();
    }
}
