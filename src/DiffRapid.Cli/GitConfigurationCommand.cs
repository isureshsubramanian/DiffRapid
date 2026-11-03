using System;
using System.IO;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunGitConfiguration(string[] args)
    {
        if (args.Length > 2 || (args.Length == 2 && args[1] != "--global"))
        {
            throw new UsageException("git-config accepts only --global.");
        }

        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "diffrapid.exe" : "diffrapid");
        Console.Out.WriteLine(GitConfiguration.Format(executable, args.Length == 2));
        return ExitSame;
    }
}
