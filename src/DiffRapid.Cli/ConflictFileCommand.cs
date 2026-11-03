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
            return ExitDifferent;
        }

        var directory = Path.GetFullPath(args.Value("out-dir") ?? Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(directory);
        WriteVersion("left", parts.Left);
        WriteVersion("right", parts.Right);
        if (parts.Base is not null)
        {
            WriteVersion("base", parts.Base);
        }

        return ExitSame;

        void WriteVersion(string side, string text)
        {
            var output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "." + side + Path.GetExtension(path));
            using (var stream = File.Create(output))
            {
                if (decoded.HasByteOrderMark)
                {
                    stream.Write(decoded.Encoding.GetPreamble());
                }

                stream.Write(decoded.Encoding.GetBytes(text));
            }

            System.Console.Out.WriteLine(output);
        }
    }
}
