using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>A file after its unpacker and prediffer pipelines have run.</summary>
public sealed class PreparedFile
{
    internal PreparedFile(string text, string compareText, IReadOnlyList<UnpackedEntry> entries, bool isBinary, string? unpackerName)
    {
        Text = text;
        CompareText = compareText;
        Entries = entries;
        IsBinary = isBinary;
        UnpackerName = unpackerName;
    }

    /// <summary>The unpacked text: what the user sees.</summary>
    public string Text { get; }

    /// <summary>The text after the prediffers: what is compared. Equals <see cref="Text"/> without prediffers.</summary>
    public string CompareText { get; }

    /// <summary>The entries of a container file when the last unpacker listed them.</summary>
    public IReadOnlyList<UnpackedEntry> Entries { get; }

    /// <summary>True when no unpacker ran and the file is not text; both texts are then empty.</summary>
    public bool IsBinary { get; }

    /// <summary>The first unpacker step that ran, or null when the file was read as plain text.</summary>
    public string? UnpackerName { get; }
}

/// <summary>Runs the unpacker and prediffer pipelines of <see cref="PluginOptions"/> before a comparison.</summary>
public static class PluginPipeline
{
    /// <summary>Reads a file and runs both pipelines on it.</summary>
    public static PreparedFile Prepare(string path, PluginOptions options, CancellationToken cancellationToken = default)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return Prepare(File.ReadAllBytes(path), path, options, cancellationToken);
    }

    /// <summary>Runs both pipelines on file content, such as an earlier revision read from version control.</summary>
    public static PreparedFile Prepare(byte[] content, string fileName, PluginOptions options, CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var registry = options.Registry ?? PluginRegistry.Default;
        var unpackSteps = ResolveUnpackerSteps(registry, options.Unpacker, fileName);
        var predifferSteps = ResolvePredifferSteps(registry, options.Prediffer);
        var unpacked = Unpack(content, fileName, options, unpackSteps, cancellationToken);
        if (unpacked.IsBinary)
        {
            return unpacked;
        }

        var compareText = RunPrediffers(unpacked.Text, fileName, options, predifferSteps, cancellationToken);
        return new PreparedFile(unpacked.Text, compareText, unpacked.Entries, false, unpacked.UnpackerName);
    }

    /// <summary>Runs only the prediffer pipeline on text that is already loaded.</summary>
    public static string ApplyPrediffers(string text, string fileName, PluginOptions options, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var steps = ResolvePredifferSteps(options.Registry ?? PluginRegistry.Default, options.Prediffer);
        return RunPrediffers(text, fileName, options, steps, cancellationToken);
    }

    private static List<(IPlugin Plugin, PluginStep Step)> ResolveUnpackerSteps(PluginRegistry registry, string? pipeline, string fileName)
    {
        var steps = new List<(IPlugin, PluginStep)>();
        if (string.IsNullOrWhiteSpace(pipeline))
        {
            return steps;
        }

        if (string.Equals(pipeline!.Trim(), PluginOptions.AutomaticUnpacker, StringComparison.OrdinalIgnoreCase))
        {
            var selected = registry.SelectUnpacker(Path.GetFileName(fileName));
            if (selected is not null)
            {
                steps.Add((selected, new PluginStep(selected.Name, Array.Empty<string>())));
            }

            return steps;
        }

        foreach (var step in PluginPipelineParser.Parse(pipeline))
        {
            IPlugin plugin = registry.FindUnpacker(step.Name) as IPlugin
                ?? registry.FindPrediffer(step.Name)
                ?? throw new FormatException($"Unknown unpacker '{step.Name}'.");
            steps.Add((plugin, step));
        }

        return steps;
    }

    private static List<(IPrediffer Plugin, PluginStep Step)> ResolvePredifferSteps(PluginRegistry registry, string? pipeline)
    {
        var steps = new List<(IPrediffer, PluginStep)>();
        if (string.IsNullOrWhiteSpace(pipeline))
        {
            return steps;
        }

        foreach (var step in PluginPipelineParser.Parse(pipeline!))
        {
            var plugin = registry.FindPrediffer(step.Name);
            if (plugin is null)
            {
                var hint = registry.FindUnpacker(step.Name) is null ? string.Empty : " It is an unpacker; use it in the unpacker pipeline.";
                throw new FormatException($"Unknown prediffer '{step.Name}'.{hint}");
            }

            steps.Add((plugin, step));
        }

        return steps;
    }

    private static PreparedFile Unpack(byte[] content, string fileName, PluginOptions options,
        List<(IPlugin Plugin, PluginStep Step)> steps, CancellationToken cancellationToken)
    {
        string? text = null;
        IReadOnlyList<UnpackedEntry> entries = Array.Empty<UnpackedEntry>();
        foreach (var (plugin, step) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = CreateContext(fileName, step, options, cancellationToken);
            if (plugin is IUnpacker unpacker)
            {
                var input = text is null ? content : Encoding.UTF8.GetBytes(text);
                using var stream = new MemoryStream(input, writable: false);
                var result = unpacker.Unpack(stream, context);
                text = result.Text;
                entries = result.Entries.Count > 0 ? result.Entries : entries;
                continue;
            }

            if (text is null)
            {
                var decoded = TextFileReader.Decode(content, overrideEncoding: options.Encoding);
                if (decoded.IsBinary)
                {
                    return new PreparedFile(string.Empty, string.Empty, entries, true, null);
                }

                text = decoded.Text;
            }

            text = ((IPrediffer)plugin).Transform(text, context);
        }

        if (text is not null)
        {
            return new PreparedFile(text, text, entries, false, steps[0].Step.Name);
        }

        var plain = TextFileReader.Decode(content, overrideEncoding: options.Encoding);
        return plain.IsBinary
            ? new PreparedFile(string.Empty, string.Empty, entries, true, null)
            : new PreparedFile(plain.Text, plain.Text, entries, false, null);
    }

    private static string RunPrediffers(string text, string fileName, PluginOptions options,
        List<(IPrediffer Plugin, PluginStep Step)> steps, CancellationToken cancellationToken)
    {
        foreach (var (plugin, step) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text = plugin.Transform(text, CreateContext(fileName, step, options, cancellationToken));
        }

        return text;
    }

    private static PluginContext CreateContext(string fileName, PluginStep step, PluginOptions options, CancellationToken cancellationToken) =>
        new(fileName ?? string.Empty, step.Arguments, options.ExternalCommandTimeout, cancellationToken);
}
