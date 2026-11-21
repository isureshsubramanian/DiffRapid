using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Cli;

internal static partial class Program
{
    /// <summary>The switches and value options one command accepts; anything else is a usage error.</summary>
    private sealed record CommandSchema(string[] Switches, string[] Values, IReadOnlyDictionary<string, string>? Refused = null)
    {
        /// <summary>The compare options every text-comparing command accepts.</summary>
        public static readonly CommandSchema Compare = new(
            ["ignore-case", "ignore-blank-lines", "ignore-eol", "ignore-numbers", "ignore-missing-trailing-eol"],
            ["algorithm", "ignore-whitespace", "line-filter", "substitute"]);

        public CommandSchema With(string[] switches, string[] values) => new([.. Switches, .. switches], [.. Values, .. values], Refused);
    }

    private static readonly Dictionary<string, CommandSchema> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = new([], []),
        ["patch"] = CommandSchema.Compare.With([], ["output", "context"]),
        ["explain"] = new(["conflicts", "no-redact"], ["endpoint", "model", "api-key-env", "azure-deployment", "api-version"],
            new Dictionary<string, string> { ["api-key"] = "Read an API key from an environment variable using --api-key-env <VAR>." }),

        // For apply, --ignore-whitespace is a switch rather than the compare option that takes a mode.
        ["apply"] = new(["reverse", "dry-run", "ignore-whitespace"], ["dir", "fuzz"]),
        ["dir"] = CommandSchema.Compare.With(["no-recurse", "json", "include-identical", "include-diffs", "renames"],
            ["method", "include", "exclude", "only", "filter-file", "report", "report-format", "similarity", "context"]),
        ["dir3"] = CommandSchema.Compare.With(["no-recurse", "json"], ["method", "include", "exclude", "filter-file"]),
        ["diff"] = CommandSchema.Compare.With(["quiet"],
            ["cp", "context", "left-label", "right-label", "unpacker", "prediffer", "rev", "report", "output"]),
        ["log"] = new([], ["count"]),
        ["history"] = CommandSchema.Compare.With(["quiet"], ["store", "label", "cp", "context", "left-label", "right-label"]),
        ["merge"] = CommandSchema.Compare.With(["diff3"], ["output", "left-label", "base-label", "right-label"]),
        ["split-conflicts"] = new([], ["out-dir"]),
        ["sync"] = CommandSchema.Compare.With(["dry-run", "yes"], ["profile", "include", "exclude", "filter-file"]),
        ["table"] = new(["no-header", "ignore-case", "trim", "by-position", "ignore-order", "quiet"],
            ["delimiter", "quote", "key", "ignore-column", "tolerance", "relative-tolerance", "report", "output"]),
        ["hex"] = new(["resync", "summary"], ["bytes-per-line", "context"]),
        ["image"] = new(["ignore-alpha"], ["tolerance", "diff-out", "max-offset"]),
        ["resolve"] = new([], ["output"]),
        ["api"] = new(["breaking-only"], []),
        ["plugins"] = new([], []),
        ["struct"] = new(["ordered"], ["format", "array-key"]),
        ["semantic"] = CommandSchema.Compare.With([], ["ignore"]),
        ["mcp"] = new([], ["root"]),
    };

    internal sealed class ParsedArgs
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["u"] = "context",
            ["n"] = "count",
            ["o"] = "output",
            ["dl"] = "left-label",
            ["dm"] = "base-label",
            ["dr"] = "right-label",
            ["ignorecase"] = "ignore-case",
            ["ignoreblanklines"] = "ignore-blank-lines",
            ["ignoreeol"] = "ignore-eol",
        };

        private readonly Dictionary<string, List<string?>> _options = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LineFilter> _lineFilters = [];
        private readonly List<SubstitutionFilter> _substitutions = [];

        /// <summary>The positional command arguments.</summary>
        public List<string> Positionals { get; } = [];

        /// <summary>Returns whether <paramref name="command"/> is a command with an option schema.</summary>
        public static bool IsCommand(string command) => Commands.ContainsKey(command);

        /// <summary>
        /// Parses the arguments of the command named by <c>args[0]</c>, from <paramref name="start"/>. Options are
        /// <c>--name</c>, <c>-n</c> and, on Windows only, <c>/name</c> when no such file exists; values follow as
        /// <c>=value</c>, <c>:value</c> or the next argument. <c>--</c> ends the options.
        /// </summary>
        /// <exception cref="UsageException">An option is unknown for the command, misses its value, or a switch has one.</exception>
        public static ParsedArgs Parse(string[] args, int start)
        {
            var command = args[0];
            if (!Commands.TryGetValue(command, out var schema))
            {
                throw new UsageException($"Unknown command '{command}'. Run 'diffrapid --help'.");
            }

            var parsed = new ParsedArgs();
            var optionsEnded = false;
            for (var i = start; i < args.Length; i++)
            {
                var argument = args[i];
                if (!optionsEnded && argument == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                var (name, inlineValue) = optionsEnded ? (null, null) : ParseOptionToken(argument);
                if (name is null)
                {
                    parsed.Positionals.Add(argument);
                    continue;
                }

                if (schema.Refused?.TryGetValue(name, out var refusal) == true)
                {
                    throw new UsageException(refusal);
                }

                var isValue = schema.Values.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!isValue && !schema.Switches.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new UsageException($"Unknown option '{argument}' for {command.ToLowerInvariant()}. Run 'diffrapid --help'.");
                }

                if (!isValue && inlineValue is not null)
                {
                    throw new UsageException($"Option '{argument}' is a switch and does not take a value.");
                }

                // A separate value may not look like another long option, so "--label --store x" is an error
                // rather than a label of "--store"; write "--label=--text" for such a value.
                if (isValue && inlineValue is null)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException($"Option '{argument}' needs a value.");
                    }

                    inlineValue = args[++i];
                }

                parsed.AddOption(name, inlineValue);
            }

            return parsed;
        }

        private static (string? Name, string? InlineValue) ParseOptionToken(string argument)
        {
            string? name = null;
            string? inlineValue = null;
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                name = argument[2..];
            }
            else if (argument.Length > 1 && (argument[0] == '-' || IsWindowsSlashOption(argument)))
            {
                name = argument[1..];
            }

            if (name is null)
            {
                return (null, null);
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

            // The legacy whitespace switch uses numeric inline values.
            if (name.Equals("ignorews", StringComparison.OrdinalIgnoreCase))
            {
                name = "ignore-whitespace";
                inlineValue = inlineValue == "2" ? "all" : "change";
            }

            return (name, inlineValue);
        }

        // WinMerge-style /o and /dl options exist for Windows tools; elsewhere "/..." is always a path.
        private static bool IsWindowsSlashOption(string argument) =>
            OperatingSystem.IsWindows() && argument[0] == '/' && !File.Exists(argument) && !Directory.Exists(argument);

        private void AddOption(string name, string? value)
        {
            if (!_options.TryGetValue(name, out var values))
            {
                values = [];
                _options[name] = values;
            }

            values.Add(value);
            if (name.Equals("line-filter", StringComparison.OrdinalIgnoreCase))
            {
                _lineFilters.Add(new LineFilter(value!));
            }
            else if (name.Equals("substitute", StringComparison.OrdinalIgnoreCase))
            {
                var equals = value!.IndexOf('=');
                if (equals < 0)
                {
                    throw new UsageException("--substitute needs <find>=<replace>.");
                }

                _substitutions.Add(new SubstitutionFilter(value[..equals], value[(equals + 1)..], true, true, false));
            }
        }

        /// <summary>Returns every value supplied for an option, in order.</summary>
        public IReadOnlyList<string> Values(string name) =>
            _options.TryGetValue(name, out var values) ? values.OfType<string>().ToList() : Array.Empty<string>();

        /// <summary>Returns whether an option was supplied.</summary>
        public bool Has(string name) => _options.ContainsKey(name);

        /// <summary>Returns the last value of an option, or null when absent.</summary>
        public string? Value(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

        /// <summary>Reads a nonnegative integer option or its fallback.</summary>
        public int IntValue(string name, int fallback)
        {
            var value = Value(name);
            if (value is null)
            {
                return fallback;
            }

            if (!int.TryParse(value, out var number) || number < 0)
            {
                throw new UsageException($"'{value}' is not a valid value for --{name}.");
            }

            return number;
        }

        /// <summary>Builds comparison settings from the parsed options.</summary>
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
                LineFilters = _lineFilters,
                Substitutions = _substitutions,
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
