using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DiffRapid.Core.Imaging;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static readonly HashSet<string> ImageValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--tolerance",
        "--diff-out",
        "--max-offset",
    };

    private static int RunImage(string[] rawArgs)
    {
        var args = ParsedArgs.Parse(JoinImageOptionValues(rawArgs), 1);
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("image needs two image files.");
        }

        var options = new ImageCompareOptions
        {
            ColorTolerance = args.IntValue("tolerance", 0),
            IgnoreAlpha = args.Has("ignore-alpha"),
            MaxOffset = args.IntValue("max-offset", 0),
        };
        if (options.ColorTolerance > ImageCompareOptions.MaxChannelTolerance)
        {
            throw new UsageException("--tolerance must be between 0 and 255.");
        }

        try
        {
            var left = ImageFile.Load(args.Positionals[0]);
            var right = ImageFile.Load(args.Positionals[1]);
            var result = ImageComparer.Compare(left, right, options);
            WriteImageSummary(args.Positionals[0], args.Positionals[1], result);
            if (args.Value("diff-out") is { } diffOut)
            {
                ImageFile.SavePng(ImageOverlay.Highlight(left, right, result), diffOut);
            }

            return result.AreIdentical ? ExitSame : ExitDifferent;
        }
        catch (NotSupportedException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Turns "--tolerance 5" into "--tolerance=5" so the shared parser keeps the value with its option.</summary>
    private static string[] JoinImageOptionValues(string[] rawArgs)
    {
        var joined = new List<string>();
        for (var index = 0; index < rawArgs.Length; index++)
        {
            var argument = rawArgs[index];
            if (index > 0 && ImageValueOptions.Contains(argument))
            {
                if (index + 1 >= rawArgs.Length)
                {
                    throw new UsageException($"Option '{argument}' needs a value.");
                }

                argument += "=" + rawArgs[++index];
            }

            joined.Add(argument);
        }

        return joined.ToArray();
    }

    private static void WriteImageSummary(string leftPath, string rightPath, ImageDiffResult result)
    {
        var output = Console.Out;
        output.WriteLine($"Left:  {leftPath} ({result.LeftWidth} x {result.LeftHeight})");
        output.WriteLine($"Right: {rightPath} ({result.RightWidth} x {result.RightHeight})");
        if (result.OffsetX != 0 || result.OffsetY != 0)
        {
            output.WriteLine($"Right image shifted by ({result.OffsetX}, {result.OffsetY})");
        }

        var percentage = result.DifferencePercentage.ToString("0.##", CultureInfo.InvariantCulture);
        output.WriteLine($"Different pixels: {result.DifferenceCount} of {result.ComparedPixelCount} ({percentage}%)");
        output.WriteLine($"Difference regions: {result.DifferenceRegions.Count}");
        foreach (var region in result.DifferenceRegions)
        {
            output.WriteLine($"  {region.X},{region.Y} {region.Width}x{region.Height}");
        }
    }
}
