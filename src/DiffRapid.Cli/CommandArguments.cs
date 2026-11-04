using System;
using System.Collections.Generic;
using System.IO;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Cli;

internal static partial class Program
{
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

        private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "algorithm",
            "ignore-whitespace",
            "context",
            "output",
            "left-label",
            "base-label",
            "right-label",
            "method",
            "include",
            "exclude",
            "only",
            "line-filter",
            "substitute",
            "cp",
            "report",
            "report-format",
            "out-dir",
            "filter-file",
            "rev",
            "count",
            "dir",
            "fuzz",
            "profile",
        };

        private readonly Dictionary<string, List<string>> _repeated = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LineFilter> _lineFilters = [];
        private readonly List<SubstitutionFilter> _substitutions = [];

        /// <summary>The positional command arguments.</summary>
        public List<string> Positionals { get; } = [];

        /// <summary>Parses command arguments and expands option aliases.</summary>
        public static ParsedArgs Parse(string[] args, int start)
        {
            var parsed = new ParsedArgs();
            for (var i = start; i < args.Length; i++)
            {
                var argument = args[i];
                var (name, inlineValue) = ParseOptionToken(argument);
                if (name is null)
                {
                    parsed.Positionals.Add(argument);
                    continue;
                }

                // For apply, --ignore-whitespace is a switch rather than the compare option that takes a mode.
                var isApplySwitch = args[0].Equals("apply", StringComparison.OrdinalIgnoreCase) && name == "ignore-whitespace";
                if (ValueOptions.Contains(name) && inlineValue is null && !isApplySwitch)
                {
                    if (i + 1 >= args.Length)
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
            else if (argument.Length > 1 && (argument[0] == '-' || (argument[0] == '/' && !File.Exists(argument) && !Directory.Exists(argument))))
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

        private void AddOption(string name, string? inlineValue)
        {
            _options[name] = inlineValue;
            if ((name is "include" or "exclude") && inlineValue is not null)
            {
                if (!_repeated.TryGetValue(name, out var masks))
                {
                    masks = [];
                    _repeated[name] = masks;
                }

                masks.Add(inlineValue);
            }
            else if (name.Equals("line-filter", StringComparison.OrdinalIgnoreCase))
            {
                _lineFilters.Add(new LineFilter(inlineValue!));
            }
            else if (name.Equals("substitute", StringComparison.OrdinalIgnoreCase))
            {
                var equals = inlineValue!.IndexOf('=');
                if (equals < 0)
                {
                    throw new UsageException("--substitute needs <find>=<replace>.");
                }

                _substitutions.Add(new SubstitutionFilter(inlineValue[..equals], inlineValue[(equals + 1)..], true, true, false));
            }
        }

        /// <summary>Returns all values supplied for a repeated option.</summary>
        public IReadOnlyList<string> Values(string name) => _repeated.TryGetValue(name, out var values) ? values : Array.Empty<string>();

        /// <summary>Returns whether an option was supplied.</summary>
        public bool Has(string name) => _options.ContainsKey(name);

        /// <summary>Returns an option value, or null when absent.</summary>
        public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

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
