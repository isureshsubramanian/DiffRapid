using System;
using System.IO;
using System.Text;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;

namespace DiffRapid.Cli;

internal static partial class Program
{
    /// <summary>
    /// Writes <c>diff --report html|md|json [-o file]</c> and returns true, or returns false when no report was
    /// asked for so the caller prints the unified diff.
    /// </summary>
    private static bool TryWriteDiffReport(ParsedArgs args, TextDiffResult result, string leftLabel, string rightLabel)
    {
        var format = args.Value("report")?.ToLowerInvariant();
        if (format is null)
        {
            return false;
        }

        if (format is not ("html" or "md" or "markdown" or "json"))
        {
            throw new UsageException($"Unknown report format '{format}'. Use html, md or json.");
        }

        var options = new FileReportOptions
        {
            LeftLabel = leftLabel,
            RightLabel = rightLabel,
            Title = "Comparison of " + Path.GetFileName(rightLabel),
            ContextLines = args.IntValue("context", 3),
        };
        var output = args.Value("output");
        using var stream = output is null ? Console.OpenStandardOutput() : File.Create(output);
        if (format == "json")
        {
            JsonReportWriter.WriteFile(result, options, stream);
            return true;
        }

        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        if (format == "html")
        {
            FileReportWriter.WriteHtml(result, options with { GeneratedAt = DateTimeOffset.UtcNow }, writer);
        }
        else
        {
            FileReportWriter.WriteMarkdown(result, options, writer);
        }

        return true;
    }

    /// <summary>Writes <c>dir --json</c> output: the versioned schema documented on <see cref="JsonReportWriter"/>.</summary>
    private static void WriteFolderJson(FolderCompareResult result)
    {
        using var stdout = Console.OpenStandardOutput();
        JsonReportWriter.WriteFolder(result, stdout);
        stdout.WriteByte((byte)'\n');
    }

    /// <summary>Writes the HTML folder report, with every changed file's diff embedded when <c>--include-diffs</c> is given.</summary>
    private static void WriteFolderHtml(ParsedArgs args, FolderCompareResult result, FolderReportOptions options, TextWriter writer)
    {
        if (!args.Has("include-diffs"))
        {
            FolderReportWriter.WriteHtml(result, options, writer);
            return;
        }

        var diffOptions = new FolderDiffReportOptions
        {
            TextOptions = args.ToCompareOptions(),
            ContextLines = args.IntValue("context", 3),
        };
        FolderDiffReportWriter.WriteHtml(result, options, diffOptions, writer);
    }
}
