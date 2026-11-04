using System;
using System.Linq;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;
using DiffRapid.Semantic;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunSemantic(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("semantic needs two C# files.");
        }

        var leftText = ReadSource(args.Positionals[0]);
        var rightText = ReadSource(args.Positionals[1]);
        var options = new MemberMatchOptions
        {
            Ignore = ParseIgnoreOptions(args.Value("ignore")),
            TextOptions = args.ToCompareOptions(),
        };
        var result = MemberMatcher.Compare(leftText, rightText, options);
        WarnAboutSyntaxErrors(result, args.Positionals[0], args.Positionals[1]);
        foreach (var root in result.Roots)
        {
            WriteMember(root, 0);
        }

        WriteUnimportantSummary(leftText, rightText, options.TextOptions);
        return result.AreEqual ? ExitSame : ExitDifferent;
    }

    private static string ReadSource(string path)
    {
        var decoded = TextFileReader.ReadFile(path);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}' looks like a binary file.");
        }

        return decoded.Text;
    }

    private static CSharpIgnoreOptions ParseIgnoreOptions(string? value)
    {
        var ignore = CSharpIgnoreOptions.None;
        if (value is null)
        {
            return ignore;
        }

        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ignore |= item.ToLowerInvariant() switch
            {
                "usings" or "using" => CSharpIgnoreOptions.UsingOrder,
                "format" or "formatting" or "whitespace" => CSharpIgnoreOptions.Formatting,
                "comments" or "comment" => CSharpIgnoreOptions.Comments,
                "regions" or "region" => CSharpIgnoreOptions.Regions,
                "all" => CSharpIgnoreOptions.All,
                _ => throw new UsageException($"Unknown --ignore value '{item}'. Use usings, format, comments, regions or all."),
            };
        }

        return ignore;
    }

    private static void WarnAboutSyntaxErrors(MemberDiffResult result, string leftPath, string rightPath)
    {
        if (result.LeftHasErrors)
        {
            Console.Error.WriteLine($"diffrapid: '{leftPath}' has syntax errors; members were matched on a best-effort parse.");
        }

        if (result.RightHasErrors)
        {
            Console.Error.WriteLine($"diffrapid: '{rightPath}' has syntax errors; members were matched on a best-effort parse.");
        }
    }

    /// <summary>Writes one line per member: status, kind, signature and what moved or was renamed.</summary>
    private static void WriteMember(MemberDiff entry, int depth)
    {
        var line = new string(' ', depth * 2) + entry.Status.ToString().PadRight(10) + " " + KindName(entry.Kind) + " " + entry.Signature;
        if (entry.Status == MemberDiffStatus.Renamed)
        {
            line += $" (was {entry.Left!.Signature})";
        }
        else if (entry.Status == MemberDiffStatus.Moved && entry.Left!.Parent?.QualifiedName != entry.Right!.Parent?.QualifiedName)
        {
            line += $" (from {entry.Left.Parent?.QualifiedName ?? "top level"})";
        }

        if (entry.Status != MemberDiffStatus.Changed && entry.TextChanged)
        {
            line += " (body changed)";
        }

        if (entry.TextDiff is { } textDiff)
        {
            var added = textDiff.ImportantHunks.Sum(hunk => hunk.RightCount);
            var removed = textDiff.ImportantHunks.Sum(hunk => hunk.LeftCount);
            line += $" +{added} -{removed}";
        }

        Console.Out.WriteLine(line);
        foreach (var child in entry.Children)
        {
            WriteMember(child, depth + 1);
        }
    }

    private static string KindName(CSharpMemberKind kind) => kind switch
    {
        CSharpMemberKind.RecordStruct => "record struct",
        CSharpMemberKind.ConversionOperator => "operator",
        CSharpMemberKind.EnumMember => "enum member",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Counts line differences that only touch trivia, comments, regions or using order.</summary>
    private static void WriteUnimportantSummary(string leftText, string rightText, CompareOptions textOptions)
    {
        var textDiff = TextComparer.Compare(TextDocument.Parse(leftText), TextDocument.Parse(rightText), textOptions);
        if (textDiff.DifferenceCount == 0)
        {
            return;
        }

        var unimportant = CSharpDiffClassifier.Classify(textDiff)
            .Where(hunk => hunk.Hunk.Significance == DiffSignificance.Important)
            .ToList();
        var reasons = unimportant
            .GroupBy(hunk => hunk.Reason)
            .Select(group => $"{group.Count()} {group.First().Description}");
        var detail = unimportant.Count == 0 ? "" : " (" + string.Join("; ", reasons) + ")";
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Line differences: {textDiff.DifferenceCount}, unimportant in C#: {unimportant.Count}{detail}");
    }
}
