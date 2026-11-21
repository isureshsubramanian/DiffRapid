using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;
using DiffRapid.Core.VersionControl;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static async Task<int> RunDiffAsync(ParsedArgs args, CancellationToken cancellationToken)
    {
        if (args.Positionals.Count != (args.Has("rev") ? 1 : 2))
        {
            throw new UsageException("diff needs two files, or one file with --rev.");
        }

        var leftPath = args.Positionals[0];
        var rightPath = args.Has("rev") ? leftPath : args.Positionals[1];
        var options = args.ToCompareOptions();
        var encoding = args.Has("cp") ? Encoding.GetEncoding(args.IntValue("cp", 0)) : null;
        var (left, right) = await LoadDiffInputsAsync(args, leftPath, rightPath, encoding, cancellationToken).ConfigureAwait(false);
        var result = TextComparer.Compare(left, right, options, cancellationToken);
        if (args.Has("rev"))
        {
            leftPath += "@" + args.Value("rev");
        }

        if (TryWriteDiffReport(args, result, args.Value("left-label") ?? leftPath, args.Value("right-label") ?? rightPath))
        {
            return result.AreEqual ? ExitSame : ExitDifferent;
        }

        if (!args.Has("quiet"))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n"
            };

            UnifiedDiffWriter.Write(
                stdout,
                result,
                args.Value("left-label") ?? leftPath,
                args.Value("right-label") ?? rightPath,
                args.IntValue("context", 3));
            stdout.Flush();
        }

        return result.AreEqual ? ExitSame : ExitDifferent;
    }

    private static IVersionControl Repository(string path) =>
        VersionControl.Find(path) ?? throw new UsageException($"'{path}' is not in a Git repository or TFVC workspace.");

    /// <summary>Reads a file's bytes as they were at <paramref name="revision"/> in its Git repository or TFVC workspace.</summary>
    private static async Task<byte[]> ReadRevisionBytesAsync(string path, string revision, CancellationToken cancellationToken)
    {
        var repository = Repository(path);
        var relative = Path.GetRelativePath(repository.Root, Path.GetFullPath(path)).Replace('\\', '/');
        return await repository.ReadFileAsync(revision, relative, cancellationToken).ConfigureAwait(false)
            ?? throw new UsageException($"{repository.Kind}: '{path}' does not exist at '{revision}'.");
    }

    private static async Task<TextDocument> LoadRevisionAsync(string path, string revision, Encoding? encoding, CancellationToken cancellationToken)
    {
        var bytes = await ReadRevisionBytesAsync(path, revision, cancellationToken).ConfigureAwait(false);
        var decoded = TextFileReader.Decode(bytes, overrideEncoding: encoding);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}@{revision}' looks like a binary file; binary compare is not available yet.");
        }

        return TextDocument.Parse(decoded.Text);
    }

    private static async Task<int> RunLogAsync(ParsedArgs args, CancellationToken cancellationToken)
    {
        if (args.Positionals.Count != 1)
        {
            throw new UsageException("log needs exactly one file.");
        }

        var repository = Repository(args.Positionals[0]);
        var history = await repository.GetHistoryAsync(args.Positionals[0], args.IntValue("count", 20), cancellationToken).ConfigureAwait(false);
        foreach (var version in history)
        {
            Console.Out.WriteLine($"{version.ShortId} {version.Date:yyyy-MM-dd} {version.Author} {version.Subject}");
        }

        return ExitSame;
    }
}
