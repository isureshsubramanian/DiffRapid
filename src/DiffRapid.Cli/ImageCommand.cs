using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DiffRapid.Core.Imaging;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunImage(ParsedArgs args)
    {
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
