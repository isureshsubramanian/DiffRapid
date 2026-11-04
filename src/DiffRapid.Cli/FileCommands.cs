using System;
using System.IO;
using System.Text;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunInfo(ParsedArgs args)
    {
        if (args.Positionals.Count != 1)
        {
            throw new UsageException("info needs exactly one file.");
        }

        Console.Out.Write(FormatInfo(TextFileReader.ReadFile(args.Positionals[0]).Info));
        return ExitSame;
    }

    internal static string FormatInfo(TextFileInfo info) =>
        $"Encoding: {info.EncodingName}\nHas BOM: {info.HasBom}\n" +
        $"Dominant line ending: {info.DominantLineEnding.ToString().ToUpperInvariant()}\n" +
        $"Mixed line endings: {info.IsMixedLineEndings}\n" +
        $"Trailing line ending: {info.HasTrailingLineEnding}\nLine count: {info.LineCount}\n";

    private static int RunMerge(ParsedArgs args)
    {
        if (args.Positionals.Count != 3)
        {
            throw new UsageException("merge needs three files: <base> <left> <right>.");
        }

        var result = ThreeWayMerger.Merge(
            Load(args.Positionals[0]),
            Load(args.Positionals[1]),
            Load(args.Positionals[2]),
            args.ToCompareOptions());

        var labels = new ConflictLabels(
            args.Value("left-label") ?? Path.GetFileName(args.Positionals[1]),
            args.Value("base-label") ?? Path.GetFileName(args.Positionals[0]),
            args.Value("right-label") ?? Path.GetFileName(args.Positionals[2]));
        var text = result.ToText(labels, includeBase: args.Has("diff3"));
        var output = args.Value("output");
        if (output is null)
        {
            Console.Out.Write(text);
        }
        else
        {
            File.WriteAllText(output, text, new UTF8Encoding(false));
        }

        if (result.HasConflicts)
        {
            Console.Error.WriteLine($"{result.ConflictCount} conflict(s) remain.");
            return ExitDifferent;
        }

        return ExitSame;
    }

    private static TextDocument Load(string path, Encoding? overrideEncoding = null)
    {
        var decoded = TextFileReader.ReadFile(path, fallback: null, overrideEncoding: overrideEncoding);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}' looks like a binary file; binary compare is not available yet.");
        }

        return TextDocument.Parse(decoded.Text);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("diffrapid: " + message);
        return ExitError;
    }

    private sealed class UsageException(string message) : Exception(message);
}
