using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunMcp(ParsedArgs args)
    {
        if (args.Positionals.Count != 0)
        {
            throw new UsageException("mcp takes no positional arguments; it prints the client registration JSON.");
        }

        var executable = OperatingSystem.IsWindows() ? "diffrapid-mcp.exe" : "diffrapid-mcp";
        var sibling = Path.Combine(AppContext.BaseDirectory, executable);
        var command = File.Exists(sibling) ? sibling : executable;
        // Each --root becomes an absolute "--root <folder>" pair for the server; without one the server
        // reads only below the directory the client starts it in.
        var serverArgs = new List<string>();
        foreach (var root in args.Values("root"))
        {
            serverArgs.Add("--root");
            serverArgs.Add(Path.GetFullPath(root));
        }

        var registration = new { mcpServers = new { diffrapid = new { command, args = serverArgs } } };
        Console.Out.WriteLine(JsonSerializer.Serialize(registration, new JsonSerializerOptions { WriteIndented = true }));
        return ExitSame;
    }
}
