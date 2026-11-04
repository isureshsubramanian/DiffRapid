using System;
using System.Collections.Generic;
using System.IO;
using DiffRapid.Core.Plugins;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunPlugins(ParsedArgs args)
    {
        if (args.Positionals.Count != 0)
        {
            throw new UsageException("plugins takes no arguments.");
        }

        Console.Out.Write(FormatPlugins(PluginRegistry.Default));
        return ExitSame;
    }

    /// <summary>Lists the unpackers and prediffers with their arguments.</summary>
    internal static string FormatPlugins(PluginRegistry registry)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        writer.WriteLine("Unpackers (--unpacker \"<name> [args] | ...\", or --unpacker auto):");
        WritePlugins(writer, registry.Unpackers);
        writer.WriteLine();
        writer.WriteLine("Prediffers (--prediffer \"<name> [args] | ...\"):");
        WritePlugins(writer, registry.Prediffers);
        writer.WriteLine();
        writer.WriteLine("Quote arguments that contain spaces or '|'. Prediffers may also follow an unpacker in --unpacker.");
        return writer.ToString();
    }

    private static void WritePlugins(TextWriter writer, IEnumerable<IPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var signature = plugin.ArgumentsHelp.Length == 0 ? plugin.Name : plugin.Name + " " + plugin.ArgumentsHelp;
            writer.WriteLine("  " + signature);
            writer.WriteLine("      " + plugin.Description);
        }
    }
}
