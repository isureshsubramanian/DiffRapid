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

        string Resolve(string path)
        {
            // Reject absolute paths, parent traversal and drive-qualified components.
            var isRooted = Path.IsPathRooted(path);
            var hasUnsafeComponent = path.Split('/', '\\').Any(component => component == ".." || component.Contains(':'));
            if (isRooted || hasUnsafeComponent)
            {
                throw new UsageException("Unsafe patch path: " + path);
            }

            var resolved = Path.GetFullPath(Path.Combine(root, path));
            // Keep resolved paths inside the selected directory.
            var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var staysInsideRoot = resolved.StartsWith(rootPrefix, comparison);
            if (!staysInsideRoot)
            {
                throw new UsageException("Patch path escapes --dir: " + path);
            }

            var parent = resolved;
            while (parent != root && parent is not null)
            {
                // Existing links can redirect otherwise safe paths outside the root.
                var entryExists = File.Exists(parent) || Directory.Exists(parent);
                var isReparsePoint = entryExists && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0;
                if (isReparsePoint)
                {
                    throw new UsageException("Patch path traverses a link: " + path);
                }

                parent = Path.GetDirectoryName(parent);
            }

            return resolved;
        }

        // Validate all paths before writing any files.
        foreach (var file in patch.Files)
        {
            if (file.OldPath is { } old)
            {
                Resolve(old);
            }

            if (file.NewPath is { } next)
            {
                Resolve(next);
            }
        }

        var failed = false;
        foreach (var file in patch.Files)
        {
            var source = options.Reverse ? file.NewPath : file.OldPath;
            var target = options.Reverse ? file.OldPath : file.NewPath;
            var path = Resolve(source ?? target ?? throw new UsageException("Patch has no file path."));
            var exists = File.Exists(path);
            var decoded = exists ? TextFileReader.ReadFile(path) : new DecodedText("", new UTF8Encoding(false), false, false);
            // Binary changes cannot be applied from a text patch; report them and carry on with the rest.
            if (decoded.IsBinary || file.IsBinary)
            {
                Console.Out.WriteLine($"{source ?? target}: skipped (binary file)");
                failed = true;
                continue;
            }

            var result = PatchApplier.Apply(decoded.Text, file, options);
            var alreadyApplied = result.Outcomes.All(o => o.Status == HunkStatus.AlreadyApplied);
            bool fileStateAllowsApply;
            if (source is null)
            {
                // An existing addition must already contain the patched content.
                var fileAdded = !exists || ((result.Outcomes.Count > 0 || decoded.Text.Length == 0) && alreadyApplied);
                fileStateAllowsApply = fileAdded;
            }
            else if (target is null)
            {
                // A deletion must consume the entire file, or have already removed it.
                var fileDeleted = (exists || alreadyApplied) && result.Text.Length == 0;
                fileStateAllowsApply = fileDeleted;
            }
            else
            {
                // A change requires an existing source file.
                var fileChanged = exists;
                fileStateAllowsApply = fileChanged;
            }

            var success = result.Succeeded && fileStateAllowsApply;
            Console.Out.WriteLine($"{source ?? target}: {(success ? "applied" : "failed")}");
            for (var i = 0; i < result.Outcomes.Count; i++)
            {
                var outcome = result.Outcomes[i];
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

            if (args.Has("dry-run"))
            {
                continue;
            }

            if (target is null)
            {
                if (exists)
                {
                    File.Delete(path);
                }

                continue;
            }

            var destination = Resolve(target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var preamble = decoded.HasByteOrderMark ? decoded.Encoding.GetPreamble() : [];
            var content = decoded.Encoding.GetBytes(result.Text);
            File.WriteAllBytes(destination, [.. preamble, .. content]);
            if (source is not null && path != destination && exists)
            {
                File.Delete(path);
            }
        }

        return failed ? ExitDifferent : ExitSame;
    }
}
