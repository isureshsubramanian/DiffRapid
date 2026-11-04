using System;
using System.IO;
using System.Text;

namespace DiffRapid.Cli;

/// <summary>
/// Headless entry point. Exit codes follow the WinMerge /enableexitcode convention:
/// 0 = identical (or merged cleanly), 1 = different (or conflicts remain), 2 = error.
/// </summary>
internal static partial class Program
{
    private const int ExitSame = 0;
    private const int ExitDifferent = 1;
    private const int ExitError = 2;

    private const string Usage =
        """
        DiffRapid command line

        Usage:
          diffrapid info <file>
          diffrapid patch <left> <right> [-o <file>] [-u <n>] [compare options]
          diffrapid apply <patchfile> [--dir <root>] [--reverse] [--fuzz <0..2>]
                          [--dry-run] [--ignore-whitespace]
          diffrapid dir <left> <right> [--method full|quick|binary|date|date-size|size|existence]
                         [--no-recurse] [--include <glob>] [--exclude <glob>] [--json]
                         [--only different|left|right|all]
                         [--filter-file <path>]  apply .flt file and folder rules
                         [--report <file>] [--report-format html|csv] [--include-identical]
                         Report format defaults to the extension (.html/.htm/.csv).
          diffrapid diff  <left> <right> [options]
          diffrapid diff <file> --rev <revision> [options]
          diffrapid log <file> [-n <count>]
                         Git: HEAD, HEAD~1, :0; TFVC: C123, T, W.
          diffrapid merge <base> <left> <right> [-o <output>] [--diff3] [options]
          diffrapid split-conflicts <file> [--out-dir <dir>]
                         Writes the left, right and (diff3) base versions of a file with conflict markers.
          diffrapid resolve <file> [-o <file>]
                         Resolves identical, whitespace-only and different-line conflicts; exit 1 if any remain.
          diffrapid git-config [--global]        print setup commands for Git Bash / POSIX shells

        Compare options:
          --algorithm <default|minimal|patience|histogram|none>   (default: histogram)
          --ignore-whitespace <change|all>      also: /ignorews:1 (change) /ignorews:2 (all)
          --ignore-case                         also: /ignorecase
          --ignore-blank-lines                  also: /ignoreblanklines
          --ignore-eol                          also: /ignoreeol
          --ignore-numbers
          --ignore-missing-trailing-eol

        diff options:
          --cp <codepage>                       force encoding for both files
          --line-filter <regex>                 ignore matching changed hunks (repeatable)
          --substitute <find>=<replace>          regex replacement for comparison (repeatable)
          -u, --context <n>                     lines of context in the unified diff (default: 3)
          --left-label <text>, --right-label <text>
          --quiet                               only set the exit code

        merge options:
          -o, --output <file>                   write the merged result to a file (default: stdout)
          --diff3                               include base text in conflict markers
          /dl <text>, /dm <text>, /dr <text>     local, base, and remote conflict labels

        Exit codes: 0 identical/clean, 1 different/conflicts, 2 error.
        """;

    /// <summary>Runs the requested command and returns its exit code.</summary>
    public static int Main(string[] args)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
            {
                Console.Out.WriteLine(Usage);
                return args.Length == 0 ? ExitError : ExitSame;
            }

            var command = args[0].ToLowerInvariant();
            if (command == "git-config")
            {
                return RunGitConfiguration(args);
            }

            var parsed = ParsedArgs.Parse(args, 1);
            return command switch
            {
                "patch" => RunPatch(parsed),
                "apply" => RunApply(parsed),
                "dir" => RunDirectory(parsed),
                "diff" => RunDiff(parsed),
                "log" => RunLog(parsed),
                "info" => RunInfo(parsed),
                "merge" => RunMerge(parsed),
                "split-conflicts" => RunSplitConflicts(parsed),
                "resolve" => RunResolve(parsed),
                _ => Fail($"Unknown command '{args[0]}'. Run 'diffrapid --help'."),
            };

        }
        catch (UsageException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (FormatException ex)
        {
            return Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(ex.Message);
        }
    }
}
