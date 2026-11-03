using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

/// <summary>
/// Headless entry point. Exit codes follow the WinMerge /enableexitcode convention:
/// 0 = identical (or merged cleanly), 1 = different (or conflicts remain), 2 = error.
/// </summary>
internal static class Program
{
    private const int ExitSame = 0;
    private const int ExitDifferent = 1;
    private const int ExitError = 2;

    private const string Usage =
        """
        DiffRapid command line

        Usage:
          diffrapid diff  <left> <right> [options]
          diffrapid merge <base> <left> <right> [-o <output>] [--diff3] [options]
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
          -u, --context <n>                     lines of context in the unified diff (default: 3)
          --left-label <text>, --right-label <text>
          --quiet                               only set the exit code

        merge options:
          -o, --output <file>                   write the merged result to a file (default: stdout)
          --diff3                               include base text in conflict markers
          /dl <text>, /dm <text>, /dr <text>     local, base, and remote conflict labels

        Exit codes: 0 identical/clean, 1 different/conflicts, 2 error.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
            {
                Console.Out.WriteLine(Usage);
                return args.Length == 0 ? ExitError : ExitSame;
            }

            var command = args[0].ToLowerInvariant();
            if (command == "git-config")
            {
                if (args.Length > 2 || (args.Length == 2 && args[1] != "--global"))
                {
                    throw new UsageException("git-config accepts only --global.");
                }

                var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "diffrapid.exe" : "diffrapid");
                Console.Out.WriteLine(GitConfiguration.Format(executable, args.Length == 2));
                return ExitSame;
            }

            var parsed = ParsedArgs.Parse(args, 1);
            return command switch
            {
                "diff" => RunDiff(parsed),
                "merge" => RunMerge(parsed),
                _ => Fail($"Unknown command '{args[0]}'. Run 'diffrapid --help'."),
            };
        }
        catch (UsageException ex)
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

    private static int RunDiff(ParsedArgs args)
    {
        if (args.Positionals.Count != 2)
        {
            throw new UsageException("diff needs exactly two files.");
        }

        var leftPath = args.Positionals[0];
        var rightPath = args.Positionals[1];
        var options = args.ToCompareOptions();

        var result = TextComparer.Compare(Load(leftPath), Load(rightPath), options);
        if (!args.Has("quiet"))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            UnifiedDiffWriter.Write(
                stdout,
                result,
                args.Value("left-label") ?? leftPath,
                args.Value("right-label") ?? rightPath,
                args.IntValue("context", 3));
            stdout.Flush();
        }

        return result.AreEqual ? ExitSame : ExitDifferent;
    }

    private static int RunMerge(ParsedArgs args)
    {
        if (args.Positionals.Count != 3)
        {
            throw new UsageException("merge needs three files: <base> <left> <right>.");
        }

        var result = ThreeWayMerger.Merge(
            Load(args.Positionals[0]),
            Load(args.Positionals[1]),
            Load(args.Positionals[2]),
            args.ToCompareOptions());

        var labels = new ConflictLabels(
            args.Value("left-label") ?? Path.GetFileName(args.Positionals[1]),
            args.Value("base-label") ?? Path.GetFileName(args.Positionals[0]),
            args.Value("right-label") ?? Path.GetFileName(args.Positionals[2]));
        var text = result.ToText(labels, includeBase: args.Has("diff3"));

        var output = args.Value("output");
        if (output is null)
        {
            Console.Out.Write(text);
        }
        else
        {
            File.WriteAllText(output, text, new UTF8Encoding(false));
        }

        if (result.HasConflicts)
        {
            Console.Error.WriteLine($"{result.ConflictCount} conflict(s) remain.");
            return ExitDifferent;
        }

        return ExitSame;
    }

    private static TextDocument Load(string path)
    {
        var decoded = TextFileReader.ReadFile(path);
        if (decoded.IsBinary)
        {
            throw new UsageException($"'{path}' looks like a binary file; binary compare is not available yet.");
        }

        return TextDocument.Parse(decoded.Text);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("diffrapid: " + message);
        return ExitError;
    }

    private sealed class UsageException(string message) : Exception(message);

    internal sealed class ParsedArgs
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["u"] = "context",
            ["o"] = "output",
            ["dl"] = "left-label",
            ["dm"] = "base-label",
            ["dr"] = "right-label",
            ["ignorecase"] = "ignore-case",
            ["ignoreblanklines"] = "ignore-blank-lines",
            ["ignoreeol"] = "ignore-eol",
        };

        private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "algorithm", "ignore-whitespace", "context", "output", "left-label", "base-label", "right-label",
        };

        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Positionals { get; } = [];

        public static ParsedArgs Parse(string[] args, int start)
        {
            var parsed = new ParsedArgs();
            for (var i = start; i < args.Length; i++)
            {
                var arg = args[i];
                string? name = null;
                string? inlineValue = null;

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    name = arg[2..];
                }
                else if (arg.Length > 1 && (arg[0] == '-' || (arg[0] == '/' && !File.Exists(arg))))
                {
                    name = arg[1..];
                }

                if (name is null)
                {
                    parsed.Positionals.Add(arg);
                    continue;
                }

                var separator = name.IndexOfAny([':', '=']);
                if (separator > 0)
                {
                    inlineValue = name[(separator + 1)..];
                    name = name[..separator];
                }

                if (Aliases.TryGetValue(name, out var canonical))
                {
                    name = canonical;
                }

                // WinMerge style: /ignorews, /ignorews:1 (change), /ignorews:2 (all).
                if (name.Equals("ignorews", StringComparison.OrdinalIgnoreCase))
                {
                    name = "ignore-whitespace";
                    inlineValue = inlineValue == "2" ? "all" : "change";
                }

                if (ValueOptions.Contains(name) && inlineValue is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException($"Option '{arg}' needs a value.");
                    }

                    inlineValue = args[++i];
                }

                parsed._options[name] = inlineValue;
            }

            return parsed;
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string? Value(string name) => _options.TryGetValue(name, out var v) ? v : null;

        public int IntValue(string name, int fallback)
        {
            var value = Value(name);
            if (value is null)
            {
                return fallback;
            }

            if (!int.TryParse(value, out var n) || n < 0)
            {
                throw new UsageException($"'{value}' is not a valid value for --{name}.");
            }

            return n;
        }

        public CompareOptions ToCompareOptions()
        {
            var algorithm = Value("algorithm")?.ToLowerInvariant() switch
            {
                null or "histogram" => DiffAlgorithmKind.Histogram,
                "default" or "myers" => DiffAlgorithmKind.Default,
                "minimal" => DiffAlgorithmKind.Minimal,
                "patience" => DiffAlgorithmKind.Patience,
                "none" => DiffAlgorithmKind.None,
                var other => throw new UsageException($"Unknown algorithm '{other}'."),
            };

            var whitespace = Value("ignore-whitespace")?.ToLowerInvariant() switch
            {
                null => Has("ignore-whitespace") ? WhitespaceMode.IgnoreChange : WhitespaceMode.Compare,
                "change" => WhitespaceMode.IgnoreChange,
                "all" => WhitespaceMode.IgnoreAll,
                var other => throw new UsageException($"Unknown whitespace mode '{other}'."),
            };

            return new CompareOptions
            {
                Algorithm = algorithm,
                Whitespace = whitespace,
                IgnoreCase = Has("ignore-case"),
                IgnoreBlankLines = Has("ignore-blank-lines"),
                IgnoreLineEndings = Has("ignore-eol"),
                IgnoreNumbers = Has("ignore-numbers"),
                IgnoreMissingTrailingLineEnding = Has("ignore-missing-trailing-eol"),
            };
        }
    }
}
