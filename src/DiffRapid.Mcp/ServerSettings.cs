namespace DiffRapid.Mcp;

/// <summary>The server's command line: <c>diffrapid-mcp [--root &lt;dir&gt;]... [--roots-from-client]</c>.</summary>
/// <param name="Paths">The allowed root folders.</param>
/// <param name="RootsFromClient">Also accept the folders the client lists through <c>roots/list</c>.</param>
/// <param name="UsesWorkingDirectory">No <c>--root</c> was given, so the working directory is the root.</param>
public sealed record ServerSettings(PathGuard Paths, bool RootsFromClient, bool UsesWorkingDirectory)
{
    /// <summary>Parses the arguments; with no <c>--root</c>, <paramref name="workingDirectory"/> is the single root.</summary>
    /// <exception cref="ArgumentException">An argument is unknown, a value is missing, or a root is not a local folder.</exception>
    public static ServerSettings Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        var roots = new List<string>();
        var rootsFromClient = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--roots-from-client")
            {
                rootsFromClient = true;
            }
            else if (argument.StartsWith("--root=", StringComparison.Ordinal))
            {
                roots.Add(argument["--root=".Length..]);
            }
            else if (argument == "--root" && index + 1 < args.Count)
            {
                roots.Add(args[++index]);
            }
            else
            {
                throw new ArgumentException(argument == "--root"
                    ? "--root needs a folder."
                    : $"Unknown argument '{argument}'. Use --root <folder> (repeatable) and --roots-from-client.");
            }
        }

        var usesWorkingDirectory = roots.Count == 0;
        return new ServerSettings(new PathGuard(usesWorkingDirectory ? [workingDirectory] : roots), rootsFromClient, usesWorkingDirectory);
    }
}
