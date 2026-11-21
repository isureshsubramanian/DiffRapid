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
        var context = args.IntValue("context", 3);
        bool changed;
        Action<TextWriter> write;
        if (Directory.Exists(left) && Directory.Exists(right))
        {
            var result = FolderComparer.Compare(left, right, new FolderCompareOptions { TextOptions = args.ToCompareOptions() });
            if (result.Root.Status == FolderCompareStatus.Error)
            {
                throw new IOException(result.Root.Error);
            }

            // The patch writer emits an entry for exactly these items.
            changed = result.Items.Any(item => item.Kind == FolderItemKind.File
                && item.Status is FolderCompareStatus.Different or FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly);
            write = writer => FolderPatchWriter.Write(writer, result, args.ToCompareOptions(), context);
        }
        else
        {
            var result = TextComparer.Compare(Load(left), Load(right), args.ToCompareOptions());
            changed = result.ImportantHunks.Any();

            // Both labels name the left file on purpose: apply then patches that file in place. Different names
            // would make the patch rename the left file to the right file's name.
            write = writer => UnifiedDiffWriter.Write(writer, result, "a/" + Path.GetFileName(left), "b/" + Path.GetFileName(left), context);
        }

        WriteText(args.Value("output"), write);

        // Like diff: 0 when the inputs match (empty patch), 1 when the patch holds changes.
        return changed ? ExitDifferent : ExitSame;
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
            var name = TerminalText.Escape((options.Reverse ? file.Patch.NewPath : file.Patch.OldPath)
                ?? (options.Reverse ? file.Patch.OldPath : file.Patch.NewPath)
                ?? throw new UsageException("Patch has no file path."));
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
                Console.Out.WriteLine($"  not written: {TerminalText.Escape(written.Error ?? string.Empty)}");
                failed = true;
            }
        }

        return failed ? ExitDifferent : ExitSame;
    }
}
