using System;
using System.Collections.Generic;
using System.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>One step of a pipeline: a plugin name and the arguments written after it.</summary>
public sealed record PluginStep(string Name, IReadOnlyList<string> Arguments);

/// <summary>
/// Parses pipelines such as <c>PrettifyJson | SortLines desc | ReplaceRegex "\d+" "#"</c>.
/// Steps are separated by <c>|</c>; arguments by white space. Double or single quotes group text that
/// contains spaces or <c>|</c>; inside double quotes <c>""</c> stands for one quote. Backslashes are literal.
/// </summary>
public static class PluginPipelineParser
{
    /// <summary>Parses a pipeline; throws <see cref="FormatException"/> when it is empty or malformed.</summary>
    public static IReadOnlyList<PluginStep> Parse(string pipeline)
    {
        if (pipeline is null)
        {
            throw new ArgumentNullException(nameof(pipeline));
        }

        if (pipeline.Trim().Length == 0)
        {
            throw new FormatException("The plugin pipeline is empty.");
        }

        var steps = new List<PluginStep>();
        var words = new List<string>();
        var position = 0;
        while (true)
        {
            SkipWhiteSpace(pipeline, ref position);
            if (position >= pipeline.Length || pipeline[position] == '|')
            {
                AddStep(steps, words, position);
                if (position >= pipeline.Length)
                {
                    break;
                }

                position++;
                continue;
            }

            words.Add(ReadWord(pipeline, ref position));
        }

        return steps;
    }

    private static void AddStep(List<PluginStep> steps, List<string> words, int position)
    {
        if (words.Count == 0)
        {
            throw new FormatException($"Empty plugin step at position {position + 1} in the plugin pipeline.");
        }

        steps.Add(new PluginStep(words[0], words.GetRange(1, words.Count - 1).ToArray()));
        words.Clear();
    }

    private static void SkipWhiteSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static string ReadWord(string text, ref int position)
    {
        var word = new StringBuilder();
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '|')
        {
            var character = text[position];
            if (character is '"' or '\'')
            {
                ReadQuoted(text, ref position, word);
                continue;
            }

            word.Append(character);
            position++;
        }

        return word.ToString();
    }

    private static void ReadQuoted(string text, ref int position, StringBuilder word)
    {
        var quote = text[position];
        var start = position;
        position++;
        while (true)
        {
            if (position >= text.Length)
            {
                throw new FormatException($"Unterminated quote starting at position {start + 1} in the plugin pipeline.");
            }

            var character = text[position];
            position++;
            if (character != quote)
            {
                word.Append(character);
                continue;
            }

            var doubled = quote == '"' && position < text.Length && text[position] == '"';
            if (!doubled)
            {
                return;
            }

            word.Append('"');
            position++;
        }
    }
}
