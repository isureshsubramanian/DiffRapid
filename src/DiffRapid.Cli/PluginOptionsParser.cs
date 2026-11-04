using System;
using System.IO;
using System.Text;
using System.Threading;
using DiffRapid.Core.Plugins;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    /// <summary>Builds plugin settings from --unpacker and --prediffer, or null when neither is given.</summary>
    internal static PluginOptions? ToPluginOptions(ParsedArgs args, Encoding? encoding)
    {
        var options = new PluginOptions
        {
            Unpacker = args.Value("unpacker"),
            Prediffer = args.Value("prediffer"),
            Encoding = encoding,
        };

        if (args.Has("unpacker") && string.IsNullOrWhiteSpace(options.Unpacker))
        {
            throw new UsageException("--unpacker needs a pipeline.");
        }

        if (args.Has("prediffer") && string.IsNullOrWhiteSpace(options.Prediffer))
        {
            throw new UsageException("--prediffer needs a pipeline.");
        }

        return options.IsEmpty ? null : options;
    }

    /// <summary>Loads both sides of a diff, running the plugin pipelines when any are given.</summary>
    private static (TextDocument Left, TextDocument Right) LoadDiffInputs(ParsedArgs args, string leftPath, string rightPath, Encoding? encoding)
    {
        var plugins = ToPluginOptions(args, encoding);
        var revision = args.Value("rev");
        if (plugins is null)
        {
            var plainLeft = revision is null ? Load(leftPath, encoding) : LoadRevision(leftPath, revision, encoding);
            return (plainLeft, Load(rightPath, encoding));
        }

        var leftBytes = revision is null ? File.ReadAllBytes(leftPath) : ReadRevisionBytes(leftPath, revision);
        var left = Prepare(leftBytes, revision is null ? leftPath : leftPath + "@" + revision, leftPath, plugins);
        var right = Prepare(File.ReadAllBytes(rightPath), rightPath, rightPath, plugins);
        return (left, right);
    }

    private static TextDocument Prepare(byte[] content, string displayName, string fileName, PluginOptions plugins)
    {
        PreparedFile prepared;
        try
        {
            prepared = PluginPipeline.Prepare(content, fileName, plugins, CancellationToken.None);
        }
        catch (PluginException ex)
        {
            throw new UsageException(ex.Message);
        }

        if (prepared.IsBinary)
        {
            throw new UsageException($"'{displayName}' looks like a binary file; use an unpacker such as --unpacker auto.");
        }

        // The command line compares and prints the transformed text.
        return TextDocument.Parse(prepared.CompareText);
    }

    private static byte[] ReadRevisionBytes(string path, string revision)
    {
        var repository = Repository(path);
        var relative = Path.GetRelativePath(repository.Root, Path.GetFullPath(path)).Replace('\\', '/');
        return repository.ReadFileAsync(revision, relative, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new UsageException($"{repository.Kind}: '{path}' does not exist at '{revision}'.");
    }
}
