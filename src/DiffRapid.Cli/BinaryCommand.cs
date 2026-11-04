using System;
using System.Globalization;
using System.IO;
using System.Text;
using DiffRapid.Core.Binary;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private const int DefaultHexContextRows = 2;

    /// <summary>Compares two files as bytes and prints the differing hex lines side by side, or a summary.</summary>
    internal static int RunHex(ParsedArgs args, TextWriter output)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("hex needs two files.");
        }

        var leftPath = Path.GetFullPath(args.Positionals[0]);
        var rightPath = Path.GetFullPath(args.Positionals[1]);
        foreach (var path in new[] { leftPath, rightPath })
        {
            if (Directory.Exists(path))
            {
                throw new UsageException($"'{path}' is a folder; hex compares two files.");
            }
        }

        var formatter = new HexFormatter(new HexFormatOptions { BytesPerLine = ReadBytesPerLine(args) });
        var context = args.IntValue("context", DefaultHexContextRows);
        var mode = args.Has("resync") ? BinaryCompareMode.Resync : BinaryCompareMode.Aligned;
        var result = BinaryComparer.CompareFiles(leftPath, rightPath, new BinaryCompareOptions { Mode = mode });

        WriteHexHeader(output, result, leftPath, rightPath);
        if (result.AreEqual)
        {
            output.WriteLine("Files are identical.");
            return ExitSame;
        }

        if (!args.Has("summary"))
        {
            WriteHexRows(output, result, leftPath, rightPath, formatter, context);
        }

        return ExitDifferent;
    }

    private static int ReadBytesPerLine(ParsedArgs args)
    {
        var bytesPerLine = args.IntValue("bytes-per-line", 16);
        if (bytesPerLine is not (8 or 16 or 32))
        {
            throw new UsageException("--bytes-per-line must be 8, 16 or 32.");
        }

        return bytesPerLine;
    }

    private static void WriteHexHeader(TextWriter output, BinaryDiffResult result, string leftPath, string rightPath)
    {
        var culture = CultureInfo.InvariantCulture;
        output.WriteLine("Mode:  " + (result.Mode == BinaryCompareMode.Resync ? "resync" : "aligned"));
        output.WriteLine(string.Format(culture, "Left:  {0} ({1:N0} bytes)", leftPath, result.LeftLength));
        output.WriteLine(string.Format(culture, "Right: {0} ({1:N0} bytes)", rightPath, result.RightLength));
        if (result.Note is not null)
        {
            output.WriteLine("Note:  " + result.Note);
        }

        if (!result.AreEqual)
        {
            output.WriteLine(string.Format(
                culture,
                "Differences: {0:N0} ({1:N0} left bytes, {2:N0} right bytes)",
                result.DifferenceCount,
                result.LeftDifferentBytes,
                result.RightDifferentBytes));
        }
    }

    private static void WriteHexRows(TextWriter output, BinaryDiffResult result, string leftPath, string rightPath, HexFormatter formatter, int context)
    {
        using var left = File.Open(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var right = File.Open(rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var digits = Math.Max(formatter.Options.OffsetDigits, formatter.FormatOffset(Math.Max(result.LeftLength, result.RightLength)).Length);
        var formatterWithWidth = new HexFormatter(formatter.Options with { OffsetDigits = digits });
        var sideWidth = digits + 2 + formatterWithWidth.HexWidth + 2 + formatter.Options.BytesPerLine;
        output.WriteLine();
        foreach (var pair in HexPairedView.Build(left, right, result, formatterWithWidth, context))
        {
            if (pair.IsGap)
            {
                output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  ... {0:N0} identical rows", pair.SkippedRows));
                continue;
            }

            var line = new StringBuilder();
            line.Append(pair.HasDifference ? "* " : "  ");
            line.Append(FormatSide(pair.Left, formatterWithWidth).PadRight(sideWidth));
            line.Append(" | ");
            line.Append(FormatSide(pair.Right, formatterWithWidth));
            output.WriteLine(line.ToString().TrimEnd());
        }
    }

    private static string FormatSide(HexLine? line, HexFormatter formatter) => line is null ? string.Empty : formatter.FormatText(line);
}
