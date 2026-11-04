using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffRapid.Core.Assemblies;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunApi(ParsedArgs args)
    {
        try
        {
            if (args.Positionals.Count == 1)
            {
                WriteApi(args.Positionals[0]);
                return ExitSame;
            }

            if (args.Positionals.Count != 2)
            {
                throw new UsageException("api needs one or two .dll or .nupkg files.");
            }

            var left = args.Positionals[0];
            var right = args.Positionals[1];
            var isPackage = IsPackage(left);
            if (isPackage != IsPackage(right))
            {
                throw new UsageException("api compares a .dll with a .dll, or a .nupkg with a .nupkg.");
            }

            var breakingOnly = args.Has("breaking-only");
            return isPackage
                ? ReportPackages(NuGetPackageReader.Read(left), NuGetPackageReader.Read(right), breakingOnly)
                : ReportAssemblies(ApiReader.Read(left), ApiReader.Read(right), breakingOnly);
        }
        catch (InvalidDataException exception)
        {
            return Fail(exception.Message);
        }
    }

    private static bool IsPackage(string path) => path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static void WriteApi(string path)
    {
        if (!IsPackage(path))
        {
            Console.Out.Write(ApiReader.Read(path).ToText());
            return;
        }

        var package = NuGetPackageReader.Read(path);
        foreach (var property in package.ToProperties())
        {
            Console.Out.WriteLine(property.Key + ": " + property.Value);
        }

        foreach (var library in package.Libraries)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("== " + library.Path);
            Console.Out.Write(library.Api.ToText());
        }
    }

    /// <summary>Exit code: 1 when there are differences (breaking ones with --breaking-only), otherwise 0.</summary>
    private static int ReportAssemblies(ApiSurface left, ApiSurface right, bool breakingOnly)
    {
        var comparison = AssemblyComparer.Compare(left, right);
        var counts = WriteComparison(comparison, breakingOnly, string.Empty);
        WriteSummary(counts.Breaking, counts.Other, breakingOnly);
        return (breakingOnly ? comparison.HasBreakingChanges : !comparison.IsIdentical) ? ExitDifferent : ExitSame;
    }

    private static int ReportPackages(NuGetPackage left, NuGetPackage right, bool breakingOnly)
    {
        var comparison = AssemblyComparer.Compare(left, right);
        var counts = WriteInfoChanges(comparison.InfoChanges, breakingOnly, string.Empty);
        foreach (var library in comparison.Libraries.Where(library => breakingOnly ? library.Comparison.HasBreakingChanges : !library.Comparison.IsIdentical))
        {
            Console.Out.WriteLine(library.Path);
            var libraryCounts = WriteComparison(library.Comparison, breakingOnly, "  ");
            counts = (counts.Breaking + libraryCounts.Breaking, counts.Other + libraryCounts.Other);
        }

        WriteSummary(counts.Breaking, counts.Other, breakingOnly);
        return (breakingOnly ? comparison.HasBreakingChanges : !comparison.IsIdentical) ? ExitDifferent : ExitSame;
    }

    private static (int Breaking, int Other) WriteComparison(ApiComparison comparison, bool breakingOnly, string indent)
    {
        var counts = WriteInfoChanges(comparison.InfoChanges, breakingOnly, indent);
        foreach (var change in comparison.Changes.Where(change => !breakingOnly || change.IsBreaking))
        {
            Console.Out.WriteLine(indent + (change.IsBreaking ? "[breaking] " : string.Empty) + change);
            counts = change.IsBreaking ? (counts.Breaking + 1, counts.Other) : (counts.Breaking, counts.Other + 1);
        }

        return counts;
    }

    private static (int Breaking, int Other) WriteInfoChanges(IReadOnlyList<ApiInfoChange> changes, bool breakingOnly, string indent)
    {
        var breaking = 0;
        var other = 0;
        foreach (var change in changes.Where(change => !breakingOnly || change.IsBreaking))
        {
            Console.Out.WriteLine(indent + (change.IsBreaking ? "[breaking] " : string.Empty) + change);
            if (change.IsBreaking)
            {
                breaking++;
            }
            else
            {
                other++;
            }
        }

        return (breaking, other);
    }

    private static void WriteSummary(int breaking, int other, bool breakingOnly)
    {
        var summary = breakingOnly ? $"{breaking} breaking change(s)." : $"{breaking} breaking, {other} other change(s).";
        Console.Out.WriteLine(summary);
    }
}
