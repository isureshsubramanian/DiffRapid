using System;

namespace DiffRapid.Cli;

internal static class GitConfiguration
{
    internal static string Format(string executable, bool global)
    {
        var scope = global ? " --global" : string.Empty;
        var tool = Quote(executable.Replace('\\', '/'));
        var diff = tool + " diff \"$LOCAL\" \"$REMOTE\"";
        var merge = tool + " merge \"$BASE\" \"$LOCAL\" \"$REMOTE\" -o \"$MERGED\"";
        return string.Join(Environment.NewLine,
            $"git config{scope} difftool.diffrapid.cmd {Quote(diff)}",
            $"git config{scope} mergetool.diffrapid.cmd {Quote(merge)}",
            $"git config{scope} mergetool.diffrapid.trustExitCode true");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
