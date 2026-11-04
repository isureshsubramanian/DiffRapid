using System;
using System.Collections.Generic;
using System.Globalization;

namespace DiffRapid.Core.Plugins;

/// <summary>The list of plugins every registry starts with.</summary>
internal static class BuiltInPlugins
{
    public static IEnumerable<IPlugin> Create()
    {
        yield return new ZipEntriesUnpacker();
        yield return new ExecPlugin();
        yield return Prediffer("PrettifyJson", "Re-indents JSON; 'sort' also orders object keys.",
            "[indent=<n>] [sort]", JsonPlugins.Prettify);
        yield return Prediffer("PrettifyXml", "Re-indents XML; comments and processing instructions are kept.",
            "[indent=<n>]", XmlPlugins.Prettify);
        yield return Prediffer("JsonQuery", "Selects part of a JSON document: .a.b, .[0], .[], .a[].b, keys.",
            "<path> [keys]", JsonPlugins.Query);
        yield return Prediffer("SortLines", "Sorts the lines (ordinal).",
            "[desc] [ignorecase]", TextPrediffers.SortLines);
        yield return Prediffer("SortLinesUnique", "Sorts the lines and drops repeated ones.",
            "[desc] [ignorecase]", TextPrediffers.SortLinesUnique);
        yield return Prediffer("IgnoreColumns", "Removes the given 1-based character columns from every line.",
            "<ranges such as 1-8,20->", ColumnPrediffers.IgnoreColumns);
        yield return Prediffer("IgnoreFields", "Blanks the given 1-based fields of delimited lines.",
            "<ranges such as 2,4-5> [delimiter=<char>|tab]", ColumnPrediffers.IgnoreFields);
        yield return Prediffer("IgnoreComments", "Removes comments; styles c (// /* */), script (#), sql (-- /* */), xml (<!-- -->).",
            "[c] [script] [sql] [xml]  (default: c)", CommentStripper.Transform);
        yield return Prediffer("IgnoreLineNumbers", "Removes a leading line number (and the spaces after it) from every line.",
            string.Empty, TextPrediffers.IgnoreLineNumbers);
        yield return Prediffer("ReplaceRegex", "Replaces regular expression matches (multi-line mode).",
            "<pattern> <replacement> [ignorecase]", TextPrediffers.ReplaceRegex);
        yield return Prediffer("ToLower", "Makes the text lower case (invariant culture).",
            string.Empty, (text, _) => text.ToLowerInvariant());
        yield return Prediffer("ToUpper", "Makes the text upper case (invariant culture).",
            string.Empty, (text, _) => text.ToUpperInvariant());
        yield return Prediffer("Tabify", "Turns leading spaces into tabs.",
            "[tabsize=<n>]  (default: 4)", TextPrediffers.Tabify);
        yield return Prediffer("Untabify", "Expands tabs to spaces at tab stops.",
            "[tabsize=<n>]  (default: 4)", TextPrediffers.Untabify);
    }

    private static IPrediffer Prediffer(string name, string description, string arguments, Func<string, PluginContext, string> transform) =>
        new DelegatePrediffer(name, description, arguments, transform);

    private sealed class DelegatePrediffer(string name, string description, string argumentsHelp, Func<string, PluginContext, string> transform) : IPrediffer
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

        public string ArgumentsHelp { get; } = argumentsHelp;

        public string Transform(string text, PluginContext context)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            return transform(text, context);
        }
    }
}

/// <summary>Reads the <c>word</c> and <c>key=value</c> arguments of a built-in step.</summary>
internal sealed class StepArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _pluginName;

    private StepArguments(string pluginName, IReadOnlyList<string> positionals)
    {
        _pluginName = pluginName;
        Positionals = positionals;
    }

    /// <summary>Arguments that are neither a known flag nor a known key=value pair, in order.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>Splits arguments into known flags, known keys and the rest.</summary>
    public static StepArguments Parse(string pluginName, IReadOnlyList<string> arguments, IEnumerable<string>? flags = null, IEnumerable<string>? keys = null)
    {
        var knownFlags = new HashSet<string>(flags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var knownKeys = new HashSet<string>(keys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var parsed = new StepArguments(pluginName, positionals);
        foreach (var argument in arguments)
        {
            var equals = argument.IndexOf('=');
            if (equals > 0 && knownKeys.Contains(argument.Substring(0, equals)))
            {
                parsed._values[argument.Substring(0, equals)] = argument.Substring(equals + 1);
            }
            else if (knownFlags.Contains(argument))
            {
                parsed._flags.Add(argument);
            }
            else
            {
                positionals.Add(argument);
            }
        }

        return parsed;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public int PositiveInt(string key, int fallback)
    {
        var value = Value(key);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new ArgumentException($"{_pluginName}: '{value}' is not a valid {key}.");
        }

        return number;
    }

    /// <summary>Throws when there are more positional arguments than the plugin takes.</summary>
    public void ExpectAtMost(int count)
    {
        if (Positionals.Count > count)
        {
            throw new ArgumentException($"{_pluginName}: unexpected argument '{Positionals[count]}'.");
        }
    }

    /// <summary>Throws unless there are exactly this many positional arguments.</summary>
    public void Expect(int count, string usage)
    {
        if (Positionals.Count != count)
        {
            throw new ArgumentException($"{_pluginName} needs {usage}.");
        }
    }
}
