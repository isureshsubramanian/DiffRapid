using System.IO;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunSplitConflicts(ParsedArgs args)
    {
        if (args.Positionals.Count != 1)
        {
            throw new UsageException("split-conflicts needs exactly one file.");
        }

        var path = Path.GetFullPath(args.Positionals[0]);
        var decoded = TextFileReader.ReadFile(path);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}' looks like a binary file.");
        }

        var parts = ConflictFile.Parse(decoded.Text);
        if (parts.ConflictCount == 0)
        {
            System.Console.Out.WriteLine("No conflicts found.");
            return ExitSame;
        }

        var directory = Path.GetFullPath(args.Value("out-dir") ?? Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(directory);
        WriteVersion("left", parts.Left);
        WriteVersion("right", parts.Right);
        if (parts.Base is not null)
        {
            WriteVersion("base", parts.Base);
        }

        // Like merge: 1 while the file still has conflicts to resolve.
        return ExitDifferent;

        void WriteVersion(string side, string text)
        {
            var output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "." + side + Path.GetExtension(path));
            WriteInOriginalEncoding(output, text, decoded);
            System.Console.Out.WriteLine(output);
        }
    }
}
