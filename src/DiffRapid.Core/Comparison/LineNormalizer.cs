using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>
/// Turns lines into integer keys so that lines considered equal under the compare options share a key.
/// One normalizer must be used for every side of a comparison.
/// </summary>
public sealed class LineNormalizer
{
    private readonly CompareOptions _options;
    private readonly Dictionary<(string Content, LineEnding Ending), int> _keys = new();
    private readonly StringBuilder _buffer = new();
    private readonly List<(Regex Pattern, string Replacement, bool Literal)> _substitutions = new();

    public LineNormalizer(CompareOptions options)
    {
        _options = options;
        foreach (var filter in options.Substitutions)
        {
            if (!filter.Enabled)
            {
                continue;
            }

            var pattern = filter.IsRegex ? filter.Find : Regex.Escape(filter.Find);
            if (filter.WholeWord)
            {
                pattern = @"(?<!\w)(?:" + pattern + @")(?!\w)";
            }

            _substitutions.Add((FilterRegex.Create(pattern, filter.CaseSensitive, filter.Find), filter.Replace, !filter.IsRegex));
        }
    }

    public int[] ToKeys(TextDocument document)
    {
        return ToKeys(document, CancellationToken.None);
    }

    /// <summary>Interns comparison keys, observing cancellation while processing the document.</summary>
    public int[] ToKeys(TextDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new int[document.LineCount];
        for (var i = 0; i < keys.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = document[i];
            var isLast = i == keys.Length - 1;
            var ending = _options.IgnoreLineEndings || (isLast && _options.IgnoreMissingTrailingLineEnding)
                ? LineEnding.None : line.Ending;
            keys[i] = Intern((NormalizeContent(line.Content), ending));
        }

        return keys;
    }

    /// <summary>True when the line is empty once whitespace is removed.</summary>
    public static bool IsBlank(string content)
    {
        foreach (var c in content)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    internal string NormalizeContent(string content)
    {
        foreach (var substitution in _substitutions)
        {
            content = substitution.Literal
                ? substitution.Pattern.Replace(content, _ => substitution.Replacement)
                : substitution.Pattern.Replace(content, substitution.Replacement);
        }

        if (_options.Whitespace == WhitespaceMode.Compare && !_options.IgnoreNumbers && !_options.IgnoreCase)
        {
            return content;
        }

        var sb = _buffer.Clear();
        var pendingSpace = false;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (char.IsWhiteSpace(c) && _options.Whitespace != WhitespaceMode.Compare)
            {
                if (_options.Whitespace == WhitespaceMode.IgnoreChange && sb.Length > 0)
                {
                    pendingSpace = true;
                }

                i++;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            if (_options.IgnoreNumbers && char.IsDigit(c))
            {
                while (i < content.Length && char.IsDigit(content[i]))
                {
                    i++;
                }

                sb.Append('#');
                continue;
            }

            sb.Append(_options.IgnoreCase ? char.ToUpperInvariant(c) : c);
            i++;
        }

        return sb.ToString();
    }

    private int Intern((string Content, LineEnding Ending) key)
    {
        if (!_keys.TryGetValue(key, out var id))
        {
            id = _keys.Count;
            _keys.Add(key, id);
        }

        return id;
    }
}
