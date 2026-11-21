using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// Unpacks a zip archive into a listing of its entries followed by the text of each text entry, in
/// name order. Each entry is also returned on its own so that archives can later be shown as folders.
/// </summary>
internal sealed class ZipEntriesUnpacker : IUnpacker
{
    /// <summary>Entries larger than this are listed but not read.</summary>
    internal const long MaxEntryLength = 16 * 1024 * 1024;

    /// <summary>
    /// The most bytes inflated across one archive (an estimate: sixteen full-size entries). Past it the
    /// archive is reported as unreadable rather than shown in part.
    /// </summary>
    internal const long MaxTotalLength = 16 * MaxEntryLength;

    private static readonly string[] Extensions = [".zip", ".jar", ".war", ".ear", ".nupkg", ".snupkg", ".vsix", ".apk"];

    public string Name => "ZipEntries";

    public string Description => "Lists a zip archive's entries and shows the text of each one (zip, jar, nupkg, vsix).";

    public string ArgumentsHelp => "[list]  (list: entry names and sizes only)";

    public bool Matches(string fileName) =>
        Extensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public UnpackResult Unpack(Stream input, PluginContext context)
    {
        var arguments = StepArguments.Parse(Name, context.Arguments, flags: ["list"]);
        arguments.ExpectAtMost(0);
        List<UnpackedEntry> entries;
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var budget = new ZipReadBudget(MaxEntryLength, MaxTotalLength);
            entries = ZipReadBudget.Entries(archive)
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .Select(entry => ReadEntry(entry, context, budget, readText: !arguments.Has("list")))
                .ToList();
        }
        catch (InvalidDataException ex)
        {
            throw new PluginException($"ZipEntries: '{context.FileName}' is not a readable zip archive ({ex.Message}).", ex);
        }

        return new UnpackResult(Format(entries), entries);
    }

    private static UnpackedEntry ReadEntry(ZipArchiveEntry entry, PluginContext context, ZipReadBudget budget, bool readText)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var name = entry.FullName.Replace('\\', '/');
        var isFolder = name.EndsWith("/", StringComparison.Ordinal);
        if (isFolder || !readText || entry.Length > MaxEntryLength)
        {
            return new UnpackedEntry(name, entry.Length, null, false);
        }

        var bytes = budget.Read(entry, context.CancellationToken);
        if (bytes is null)
        {
            return new UnpackedEntry(name, entry.Length, null, false);
        }

        var decoded = TextFileReader.Decode(bytes);
        return decoded.IsBinary
            ? new UnpackedEntry(name, entry.Length, null, true)
            : new UnpackedEntry(name, entry.Length, decoded.Text, false);
    }

    private static string Format(IReadOnlyList<UnpackedEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Name).Append('\t').Append(entry.Length).Append(" bytes");
            if (entry.IsBinary)
            {
                builder.Append(" (binary)");
            }

            builder.Append('\n');
        }

        foreach (var entry in entries.Where(entry => entry.Text is not null))
        {
            builder.Append('\n').Append("=== ").Append(entry.Name).Append(" ===").Append('\n').Append(entry.Text);
            if (entry.Text!.Length > 0 && entry.Text[entry.Text.Length - 1] is not '\n' and not '\r')
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
