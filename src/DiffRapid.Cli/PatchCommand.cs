using System;
using System.IO;
using System.Linq;
using System.Text;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Patching;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunPatch(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("patch needs two files or two directories.");
        }

        var left = args.Positionals[0];
        var right = args.Positionals[1];
        using var buffer = new StringWriter();
        var context = args.IntValue("context", 3);
        if (Directory.Exists(left) && Directory.Exists(right))
        {
            var result = FolderComparer.Compare(left, right, new FolderCompareOptions { TextOptions = args.ToCompareOptions() });
            if (result.Root.Status == FolderCompareStatus.Error)
            {
                throw new IOException(result.Root.Error);
            }

            FolderPatchWriter.Write(buffer, result, args.ToCompareOptions(), context);
        }
        else
        {
            UnifiedDiffWriter.Write(buffer, TextComparer.Compare(Load(left), Load(right), args.ToCompareOptions()),
                "a/" + Path.GetFileName(left), "b/" + Path.GetFileName(left), context);
        }

        if (args.Value("output") is { } output)
        {
            File.WriteAllText(output, buffer.ToString(), new UTF8Encoding(false));
        }
        else
        {
            Console.Out.Write(buffer.ToString());
        }

        return ExitSame;
    }

    private static int RunApply(ParsedArgs args)
    {
        if (args.Positionals.Count != 1)
        {
            throw new UsageException("apply needs one patch file.");
        }

        var patch = Patch.Parse(TextFileReader.ReadFile(args.Positionals[0]).Text);
        var root = Path.GetFullPath(args.Value("dir") ?? Environment.CurrentDirectory);
        var options = new PatchApplyOptions { Fuzz = args.IntValue("fuzz", 2), Reverse = args.Has("reverse"), IgnoreWhitespace = args.Has("ignore-whitespace") };
        if (options.Fuzz > 2)
        {
            throw new UsageException("--fuzz must be between 0 and 2.");
        }

        var plan = FolderPatchApplier.Plan(patch, root, options);

        // An unsafe path rejects the whole patch before any file is written.
        if (plan.Files.FirstOrDefault(f => f.Status == FilePatchStatus.Unsafe) is { } unsafePath)
        {
            throw new UsageException(unsafePath.Message!);
        }

        var failed = false;
        foreach (var file in plan.Files)
        {
            var name = (options.Reverse ? file.Patch.NewPath : file.Patch.OldPath)
                ?? (options.Reverse ? file.Patch.OldPath : file.Patch.NewPath)
                ?? throw new UsageException("Patch has no file path.");
            if (file.Status == FilePatchStatus.Binary)
            {
                Console.Out.WriteLine($"{name}: skipped (binary file)");
                failed = true;
                continue;
            }

            var success = file.Status is FilePatchStatus.Ready or FilePatchStatus.AlreadyApplied;
            Console.Out.WriteLine($"{name}: {(success ? "applied" : "failed")}");
            for (var i = 0; i < file.Outcomes.Count; i++)
            {
                var outcome = file.Outcomes[i];
                if (outcome.Status != HunkStatus.Applied)
                {
                    Console.Out.WriteLine($"  hunk {i + 1}: {outcome.Status} ({outcome.Offset})");
                }
            }

            if (!success)
            {
                failed = true;
                continue;
            }

            if (args.Has("dry-run") || !file.CanApply)
            {
                continue;
            }

            var written = FolderPatchApplier.ApplyFile(file);
            if (!written.Written)
            {
                Console.Out.WriteLine($"  not written: {written.Error}");
                failed = true;
            }
        }

        return failed ? ExitDifferent : ExitSame;
    }
}
