namespace DiffRapid.Mcp;

/// <summary>The server's command line: <c>diffrapid-mcp [--root &lt;dir&gt;]...</c>.</summary>
/// <param name="Paths">The allowed root folders.</param>
/// <param name="UsesWorkingDirectory">No <c>--root</c> was given, so the working directory is the root.</param>
public sealed record ServerSettings(PathGuard Paths, bool UsesWorkingDirectory)
{
    /// <summary>Parses the arguments; with no <c>--root</c>, <paramref name="workingDirectory"/> is the single root.</summary>
    /// <exception cref="ArgumentException">An argument is unknown, a value is missing, or a root is not a local folder.</exception>
    public static ServerSettings Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        var roots = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--root=", StringComparison.Ordinal))
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
                    : $"Unknown argument '{argument}'. Use --root <folder> (repeatable).");
            }
        }

        var usesWorkingDirectory = roots.Count == 0;
        return new ServerSettings(new PathGuard(usesWorkingDirectory ? [workingDirectory] : roots), usesWorkingDirectory);
    }
}
