using System;
using System.IO;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunResolve(ParsedArgs args)
    {
        if (args.Positionals.Count != 1)
        {
            throw new UsageException("resolve needs exactly one file.");
        }

        var path = Path.GetFullPath(args.Positionals[0]);
        var decoded = TextFileReader.ReadFile(path);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}' looks like a binary file.");
        }

        var resolution = ConflictResolver.ResolveConflictText(decoded.Text);
        if (args.Value("output") is { } output)
        {
            WriteResolved(Path.GetFullPath(output), resolution.Text, decoded);
        }
        else
        {
            Console.Out.Write(resolution.Text);
        }

        foreach (var resolved in resolution.Resolutions)
        {
            Console.Error.WriteLine($"Conflict {resolved.Chunk + 1}: resolved ({DescribeResolution(resolved.Kind)}).");
        }

        Console.Error.WriteLine(
            $"{resolution.ResolvedCount} of {resolution.ConflictCount} conflict(s) resolved, {resolution.RemainingCount} remain.");
        return resolution.RemainingCount > 0 ? ExitDifferent : ExitSame;
    }

    private static string DescribeResolution(SimpleResolutionKind kind)
    {
        return kind switch
        {
            SimpleResolutionKind.SameChange => "both sides made the same change",
            SimpleResolutionKind.LeftWhitespaceOnly => "left changed only whitespace",
            SimpleResolutionKind.RightWhitespaceOnly => "right changed only whitespace",
            _ => "the sides changed different lines",
        };
    }

    // Writes in the encoding the conflict file was read with, keeping its byte order mark.
    private static void WriteResolved(string path, string text, DecodedText decoded)
    {
        using var stream = File.Create(path);
        if (decoded.HasByteOrderMark)
        {
            stream.Write(decoded.Encoding.GetPreamble());
        }

        stream.Write(decoded.Encoding.GetBytes(text));
    }
}
