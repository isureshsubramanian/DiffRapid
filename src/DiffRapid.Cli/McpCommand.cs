using System;
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
        var registration = new { mcpServers = new { diffrapid = new { command, args = Array.Empty<string>() } } };
        Console.Out.WriteLine(JsonSerializer.Serialize(registration, new JsonSerializerOptions { WriteIndented = true }));
        return ExitSame;
    }
}
