using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private static async Task<(TextDocument Left, TextDocument Right)> LoadDiffInputsAsync(ParsedArgs args, string leftPath, string rightPath,
        Encoding? encoding, CancellationToken cancellationToken)
    {
        var plugins = ToPluginOptions(args, encoding);
        var revision = args.Value("rev");
        if (plugins is null)
        {
            var plainLeft = revision is null
                ? LoadDocumentOrText(leftPath, encoding)
                : await LoadRevisionAsync(leftPath, revision, encoding, cancellationToken).ConfigureAwait(false);
            return (plainLeft, LoadDocumentOrText(rightPath, encoding));
        }

        var leftBytes = revision is null
            ? await File.ReadAllBytesAsync(leftPath, cancellationToken).ConfigureAwait(false)
            : await ReadRevisionBytesAsync(leftPath, revision, cancellationToken).ConfigureAwait(false);
        var left = Prepare(leftBytes, revision is null ? leftPath : leftPath + "@" + revision, leftPath, plugins, cancellationToken);
        var rightBytes = await File.ReadAllBytesAsync(rightPath, cancellationToken).ConfigureAwait(false);
        var right = Prepare(rightBytes, rightPath, rightPath, plugins, cancellationToken);
        return (left, right);
    }

    private static TextDocument Prepare(byte[] content, string displayName, string fileName, PluginOptions plugins, CancellationToken cancellationToken)
    {
        PreparedFile prepared;
        try
        {
            prepared = PluginPipeline.Prepare(content, fileName, plugins, cancellationToken);
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
}
