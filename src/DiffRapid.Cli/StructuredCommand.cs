using System;
using System.IO;
using System.Text;
using DiffRapid.Core.Structured;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private const int MaxShownValueLength = 80;

    private static int RunStructured(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("struct needs two files.");
        }

        var leftPath = args.Positionals[0];
        var rightPath = args.Positionals[1];
        var leftText = TextFileReader.ReadFile(leftPath).Text;
        var rightText = TextFileReader.ReadFile(rightPath).Text;
        var format = ParseStructuredFormat(args.Value("format"));
        if (format == StructuredFormat.Auto)
        {
            format = StructuredReader.DetectFormat(leftPath, leftText);
        }

        var arrayKey = args.Value("array-key");
        var left = ReadStructured(leftPath, leftText, format, arrayKey);
        var right = ReadStructured(rightPath, rightText, format, arrayKey);
        var differences = StructuredComparer.Compare(left, right, ignoreOrder: !args.Has("ordered"));
        if (differences.Count == 0)
        {
            Console.Out.WriteLine("No structural differences.");
            return ExitSame;
        }

        foreach (var difference in differences)
        {
            Console.Out.WriteLine(FormatDifference(difference));
        }

        Console.Out.WriteLine($"{differences.Count} difference(s).");
        return ExitDifferent;
    }

    private static StructuredFormat ParseStructuredFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "auto" => StructuredFormat.Auto,
            "xml" => StructuredFormat.Xml,
            "json" => StructuredFormat.Json,
            "yaml" or "yml" => StructuredFormat.Yaml,
            "msbuild" => StructuredFormat.MSBuild,
            "resx" => StructuredFormat.Resx,
            var other => throw new UsageException($"Unknown format '{other}'. Use auto, xml, json, yaml, msbuild or resx."),
        };

    private static StructuredNode ReadStructured(string path, string text, StructuredFormat format, string? arrayKey)
    {
        try
        {
            return StructuredReader.Read(text, format, arrayKey);
        }
        catch (StructuredFormatException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Formats one difference as "Kind  path  (left lines, right lines)  old -> new".</summary>
    internal static string FormatDifference(StructuredDifference difference)
    {
        var line = new StringBuilder();
        line.Append(difference.Kind.ToString().PadRight(8)).Append(' ').Append(difference.Path);
        if (difference.Kind == StructuredChangeKind.Renamed)
        {
            line.Append(" -> ").Append(difference.Right!.Path);
        }

        line.Append("  (");
        if (difference.LeftSpan is { } leftSpan)
        {
            line.Append("left ").Append(leftSpan);
        }

        if (difference.LeftSpan is not null && difference.RightSpan is not null)
        {
            line.Append(", ");
        }

        if (difference.RightSpan is { } rightSpan)
        {
            line.Append("right ").Append(rightSpan);
        }

        line.Append(')');
        if (difference.Kind == StructuredChangeKind.Changed)
        {
            line.Append("  ").Append(ShowValue(difference.Left!)).Append(" -> ").Append(ShowValue(difference.Right!));
        }

        return line.ToString();
    }

    private static string ShowValue(StructuredNode node)
    {
        if (node.Value is null)
        {
            return node.Kind.ToString().ToLowerInvariant();
        }

        var value = node.Value.Replace("\r", "\\r").Replace("\n", "\\n");
        if (value.Length > MaxShownValueLength)
        {
            value = value[..MaxShownValueLength] + "...";
        }

        var isLiteral = node.Kind is StructuredNodeKind.Number or StructuredNodeKind.Boolean or StructuredNodeKind.Null;
        return isLiteral ? value : "\"" + value + "\"";
    }
}
