using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>An inclusive 1-based range; <see cref="Last"/> is <see cref="int.MaxValue"/> for an open end.</summary>
internal readonly record struct OneBasedRange(int First, int Last)
{
    public bool Contains(int position) => position >= First && position <= Last;

    /// <summary>Parses ranges such as <c>1-8,20-</c>, <c>-4</c> or <c>3</c>.</summary>
    public static IReadOnlyList<OneBasedRange> ParseList(string text, string pluginName)
    {
        var ranges = new List<OneBasedRange>();
        foreach (var part in text.Split(','))
        {
            var trimmed = part.Trim();
            var dash = trimmed.IndexOf('-');
            var first = dash < 0 ? trimmed : trimmed.Substring(0, dash);
            var last = dash < 0 ? trimmed : trimmed.Substring(dash + 1);
            var range = new OneBasedRange(
                first.Length == 0 && dash >= 0 ? 1 : ParseNumber(first, text, pluginName),
                last.Length == 0 && dash >= 0 ? int.MaxValue : ParseNumber(last, text, pluginName));
            if (range.First > range.Last || (dash == 0 && last.Length == 0))
            {
                throw new ArgumentException($"{pluginName}: '{trimmed}' is not a valid range.");
            }

            ranges.Add(range);
        }

        return ranges;
    }

    private static int ParseNumber(string value, string text, string pluginName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new ArgumentException($"{pluginName}: '{text}' is not a valid list of ranges such as 1-8,20-.");
        }

        return number;
    }
}

/// <summary>Prediffers that drop fixed columns or delimited fields from every line.</summary>
internal static class ColumnPrediffers
{
    public static string IgnoreColumns(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("IgnoreColumns", context.Arguments);
        arguments.Expect(1, "a list of column ranges such as 1-8,20-");
        var ranges = OneBasedRange.ParseList(arguments.Positionals[0], "IgnoreColumns");
        return TextLines.Map(text, line => RemoveColumns(line, ranges));
    }

    public static string IgnoreFields(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("IgnoreFields", context.Arguments, keys: ["delimiter"]);
        arguments.Expect(1, "a list of field ranges such as 2,4-5");
        var ranges = OneBasedRange.ParseList(arguments.Positionals[0], "IgnoreFields");
        var delimiter = Delimiter(arguments.Value("delimiter"));
        return TextLines.Map(text, line => BlankFields(line, ranges, delimiter));
    }

    private static char Delimiter(string? value)
    {
        if (value is null)
        {
            return ',';
        }

        if (value.Equals("tab", StringComparison.OrdinalIgnoreCase) || value == "\\t")
        {
            return '\t';
        }

        if (value.Length != 1)
        {
            throw new ArgumentException($"IgnoreFields: the delimiter must be one character or 'tab', not '{value}'.");
        }

        return value[0];
    }

    private static string RemoveColumns(string line, IReadOnlyList<OneBasedRange> ranges)
    {
        var builder = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var column = i + 1;
            if (!ranges.Any(range => range.Contains(column)))
            {
                builder.Append(line[i]);
            }
        }

        return builder.ToString();
    }

    private static string BlankFields(string line, IReadOnlyList<OneBasedRange> ranges, char delimiter)
    {
        // Fields are split on every delimiter; quoting is not interpreted.
        var fields = line.Split(delimiter);
        for (var i = 0; i < fields.Length; i++)
        {
            var field = i + 1;
            if (ranges.Any(range => range.Contains(field)))
            {
                fields[i] = string.Empty;
            }
        }

        return string.Join(delimiter.ToString(), fields);
    }
}
