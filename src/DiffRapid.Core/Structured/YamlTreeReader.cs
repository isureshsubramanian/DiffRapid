using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiffRapid.Core.Structured;

/// <summary>
/// Reads the common block-style subset of YAML into a structured tree: maps, sequences (also "- key: value"
/// items and sequences written at their key's indentation), plain, single- and double-quoted scalars,
/// literal (|) and folded (&gt;) block scalars with - and + chomping, flow collections that fit on one line,
/// comments, and a single document with optional --- and ... markers. Scalars are typed by the YAML 1.2 core
/// schema (null, true/false, numbers, otherwise strings).
/// </summary>
/// <remarks>
/// Not supported, and reported as an error with its line: anchors and aliases (&amp;, *), tags (!), directives
/// (%), complex keys (?), several documents in one file, quoted or plain scalars that continue on the next
/// line, flow collections spread over several lines, block scalar indentation indicators, and tab indentation.
/// Duplicate keys in a map are an error.
/// </remarks>
public static class YamlTreeReader
{
    /// <summary>Reads YAML text; the root node is the document's top-level value.</summary>
    /// <param name="text">The YAML text.</param>
    /// <param name="arrayKey">A key that identifies the maps inside sequences, or null to match items by position.</param>
    /// <param name="cancellationToken">Stops reading a large file.</param>
    /// <exception cref="StructuredFormatException">The text is malformed or uses an unsupported feature.</exception>
    public static StructuredNode Read(string text, string? arrayKey = null, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return new Parser(text, cancellationToken).ParseDocument().Freeze(arrayKey, cancellationToken);
    }

    private const string NullValue = "null";

    private static readonly Regex NumberPattern = new(
        @"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|0x[0-9a-fA-F]+|0o[0-7]+|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$",
        RegexOptions.CultureInvariant);

    /// <summary>The type a plain (unquoted) scalar has under the YAML 1.2 core schema.</summary>
    internal static StructuredNodeKind PlainKind(string text)
    {
        if (text is "" or "~" or "null" or "Null" or "NULL")
        {
            return StructuredNodeKind.Null;
        }

        if (text is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return StructuredNodeKind.Boolean;
        }

        return NumberPattern.IsMatch(text) ? StructuredNodeKind.Number : StructuredNodeKind.String;
    }

    private sealed class Line(int number, string raw)
    {
        public int Number { get; } = number;

        public string Raw { get; } = raw;

        /// <summary>Spaces before the content; moved right when a "- " item holds a map or sequence.</summary>
        public int Indent { get; set; }

        /// <summary>The text after the indentation, without its comment or trailing spaces.</summary>
        public string Content { get; set; } = string.Empty;

        public bool HasTabIndent { get; set; }
    }

    private sealed record MapKey(string Name, int ValueStart);

    private sealed class Parser
    {
        private readonly List<Line> _lines = [];
        private readonly CancellationToken _cancellationToken;
        private int _index;
        private bool _sawContent;
        private bool _ended;

        public Parser(string text, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < rawLines.Length; i++)
            {
                _lines.Add(Prepare(new Line(i + 1, rawLines[i])));
            }
        }

        public NodeBuilder ParseDocument()
        {
            var first = Peek();
            if (first is null)
            {
                return new NodeBuilder(StructuredNodeKind.Null, string.Empty, 1) { Value = NullValue };
            }

            var root = ParseBlock(first.Indent, string.Empty, first.Number, 1);
            var rest = Peek();
            if (rest is not null)
            {
                throw Error(rest, rest.Indent, rest.Indent > first.Indent ? "unexpected indentation" : "unexpected content after the top-level value");
            }

            return root;
        }

        private static Line Prepare(Line line)
        {
            var indent = 0;
            while (indent < line.Raw.Length && line.Raw[indent] == ' ')
            {
                indent++;
            }

            var afterSpaces = indent;
            while (afterSpaces < line.Raw.Length && line.Raw[afterSpaces] == '\t')
            {
                afterSpaces++;
            }

            line.Indent = indent;
            line.Content = StripComment(line.Raw.Substring(afterSpaces)).TrimEnd();
            line.HasTabIndent = afterSpaces > indent;
            return line;
        }

        /// <summary>Removes a # comment that is outside quotes and starts the line or follows whitespace.</summary>
        private static string StripComment(string text)
        {
            char quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var current = text[i];
                if (quote != '\0')
                {
                    if (quote == '"' && current == '\\')
                    {
                        i++;
                    }
                    else if (current == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                var startsToken = i == 0 || text[i - 1] is ' ' or '[' or '{' or ',';
                if ((current == '"' || current == '\'') && startsToken)
                {
                    quote = current;
                }
                else if (current == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
                {
                    return text.Substring(0, i);
                }
            }

            return text;
        }

        /// <summary>The next line with content, after blank lines, comments and document markers.</summary>
        private Line? Peek()
        {
            while (_index < _lines.Count)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var line = _lines[_index];
                if (line.Content.Length == 0)
                {
                    _index++;
                    continue;
                }

                if (line.HasTabIndent)
                {
                    throw Error(line, line.Indent, "tabs cannot be used for indentation");
                }

                if (line.Indent == 0 && IsDocumentMarker(line))
                {
                    _index++;
                    continue;
                }

                if (_ended)
                {
                    throw Error(line, line.Indent, "content after the end of the document ('...')");
                }

                _sawContent = true;
                return line;
            }

            return null;
        }

        /// <summary>Returns true for a document marker (--- or ...), which is skipped; rejects what is not supported.</summary>
        private bool IsDocumentMarker(Line line)
        {
            var content = line.Content;
            if (content.StartsWith("%", StringComparison.Ordinal) && !_sawContent)
            {
                throw Error(line, 0, "directives are not supported");
            }

            if (content == "---" || content.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (_sawContent || _ended)
                {
                    throw Error(line, 0, "several documents in one file are not supported");
                }

                if (content != "---")
                {
                    throw Error(line, 4, "content on the '---' line is not supported");
                }

                return true;
            }

            if (content == "...")
            {
                _ended = true;
                return true;
            }

            return false;
        }

        private NodeBuilder ParseBlock(int indent, string name, int startLine, int depth)
        {
            CheckDepth(depth);
            var line = Peek()!;
            if (IsSequenceItem(line.Content))
            {
                return ParseSequence(indent, name, startLine, depth);
            }

            if (FindMapKey(line) is not null)
            {
                return ParseMap(indent, name, startLine, depth);
            }

            _index++;
            var scalar = ParseInline(line, 0, name, startLine, depth);
            RejectContinuation(indent - 1);
            return scalar;
        }

        private NodeBuilder ParseMap(int indent, string name, int startLine, int depth)
        {
            var map = new NodeBuilder(StructuredNodeKind.Object, name, startLine);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var line = Peek(); line is not null && line.Indent >= indent; line = Peek())
            {
                if (line.Indent > indent)
                {
                    throw Error(line, line.Indent, "unexpected indentation");
                }

                var key = FindMapKey(line) ?? throw Error(line, line.Indent, "expected 'key: value'");
                if (!names.Add(key.Name))
                {
                    throw Error(line, line.Indent, $"duplicate key '{key.Name}'");
                }

                _index++;
                var value = ParseValue(line, key.ValueStart, indent, key.Name, depth + 1, allowCompactSequence: true);
                map.Add(value);
                map.EndLine = value.EndLine;
            }

            return map;
        }

        private NodeBuilder ParseSequence(int indent, string name, int startLine, int depth)
        {
            var sequence = new NodeBuilder(StructuredNodeKind.Array, name, startLine);
            for (var line = Peek(); line is not null && line.Indent >= indent; line = Peek())
            {
                if (line.Indent > indent)
                {
                    throw Error(line, line.Indent, "unexpected indentation");
                }

                if (!IsSequenceItem(line.Content))
                {
                    break;
                }

                var item = ParseSequenceItem(line, indent, depth + 1);
                sequence.Add(item);
                sequence.EndLine = item.EndLine;
            }

            return sequence;
        }

        private NodeBuilder ParseSequenceItem(Line line, int indent, int depth)
        {
            var offset = 1;
            while (offset < line.Content.Length && line.Content[offset] == ' ')
            {
                offset++;
            }

            var rest = line.Content.Substring(offset);
            var nested = new Line(line.Number, line.Raw) { Indent = line.Indent + offset, Content = rest };
            if (rest.Length > 0 && (IsSequenceItem(rest) || FindMapKey(nested) is not null))
            {
                // "- key: value" and "- - item" hold a block whose indentation starts after the dash.
                line.Indent = nested.Indent;
                line.Content = rest;
                return ParseBlock(line.Indent, string.Empty, line.Number, depth);
            }

            _index++;
            return ParseValue(line, offset, indent, string.Empty, depth, allowCompactSequence: false);
        }

        /// <summary>Parses what follows "key:" or "- ": an inline value, a block scalar, or a nested block.</summary>
        private NodeBuilder ParseValue(Line line, int valueStart, int indent, string name, int depth, bool allowCompactSequence)
        {
            CheckDepth(depth);
            var rest = line.Content.Substring(valueStart).Trim();
            if (rest.Length > 0 && (rest[0] == '|' || rest[0] == '>'))
            {
                return ParseBlockScalar(line, valueStart, indent, name, rest);
            }

            if (rest.Length > 0)
            {
                var start = valueStart;
                while (line.Content[start] == ' ')
                {
                    start++;
                }

                var inline = ParseInline(line, start, name, line.Number, depth);
                RejectContinuation(indent);
                return inline;
            }

            var next = Peek();
            if (next is not null && next.Indent > indent)
            {
                return ParseBlock(next.Indent, name, line.Number, depth);
            }

            if (allowCompactSequence && next is not null && next.Indent == indent && IsSequenceItem(next.Content))
            {
                return ParseSequence(indent, name, line.Number, depth);
            }

            return new NodeBuilder(StructuredNodeKind.Null, name, line.Number) { Value = NullValue };
        }

        private void RejectContinuation(int indent)
        {
            var next = Peek();
            if (next is not null && next.Indent > indent)
            {
                throw Error(next, next.Indent, "unexpected indentation (scalars that continue on the next line are not supported)");
            }
        }

        private NodeBuilder ParseInline(Line line, int start, string name, int startLine, int depth)
        {
            var text = line.Content.Substring(start);
            if (text[0] == '[' || text[0] == '{')
            {
                var flow = new FlowParser(this, line, start, depth);
                var node = flow.ParseValue(name);
                flow.ExpectEnd();
                return node;
            }

            var (kind, value) = ParseScalar(line, start, text);
            return new NodeBuilder(kind, name, startLine) { Value = value, EndLine = line.Number };
        }

        private (StructuredNodeKind Kind, string Value) ParseScalar(Line line, int start, string text)
        {
            switch (text[0])
            {
                case '"':
                case '\'':
                    var end = 0;
                    var value = ReadQuoted(line, start, text, ref end);
                    if (end != text.Length)
                    {
                        throw Error(line, line.Indent + start + end, "unexpected text after the quoted scalar");
                    }

                    return (StructuredNodeKind.String, value);
                case '&':
                case '*':
                    throw Error(line, line.Indent + start, "anchors and aliases are not supported");
                case '!':
                    throw Error(line, line.Indent + start, "tags are not supported");
                case '?':
                    throw Error(line, line.Indent + start, "complex keys are not supported");
                case '%':
                case '@':
                case '`':
                    throw Error(line, line.Indent + start, $"a plain scalar cannot start with '{text[0]}'");
                default:
                    var kind = PlainKind(text);

                    // ~, Null and an empty value are all null; True and TRUE are true.
                    var normalized = kind switch
                    {
                        StructuredNodeKind.Null => NullValue,
                        StructuredNodeKind.Boolean => text.ToLowerInvariant(),
                        _ => text,
                    };
                    return (kind, normalized);
            }
        }

        /// <summary>Reads a quoted scalar that starts at text[0]; <paramref name="end"/> is set past the closing quote.</summary>
        private string ReadQuoted(Line line, int start, string text, ref int end)
        {
            var quote = text[0];
            var builder = new StringBuilder();
            var position = 1;
            while (position < text.Length)
            {
                var current = text[position];
                if (current == quote)
                {
                    if (quote == '\'' && position + 1 < text.Length && text[position + 1] == '\'')
                    {
                        builder.Append('\'');
                        position += 2;
                        continue;
                    }

                    end = position + 1;
                    return builder.ToString();
                }

                if (quote == '"' && current == '\\')
                {
                    position = ReadEscape(line, start, text, position, builder);
                    continue;
                }

                builder.Append(current);
                position++;
            }

            throw Error(line, line.Indent + start, "the quoted scalar is not closed on this line (multi-line quoted scalars are not supported)");
        }

        private int ReadEscape(Line line, int start, string text, int position, StringBuilder builder)
        {
            if (position + 1 >= text.Length)
            {
                throw Error(line, line.Indent + start + position, "the quoted scalar is not closed on this line");
            }

            var escape = text[position + 1];
            var simple = escape switch
            {
                '0' => "\0",
                'a' => "\a",
                'b' => "\b",
                't' => "\t",
                'n' => "\n",
                'v' => "\v",
                'f' => "\f",
                'r' => "\r",
                'e' => "\u001b",
                ' ' => " ",
                '"' => "\"",
                '/' => "/",
                '\\' => "\\",
                '_' => "\u00a0",
                _ => null,
            };
            if (simple is not null)
            {
                builder.Append(simple);
                return position + 2;
            }

            var length = escape switch
            {
                'x' => 2,
                'u' => 4,
                'U' => 8,
                _ => 0,
            };
            var digits = length > 0 && position + 2 + length <= text.Length ? text.Substring(position + 2, length) : string.Empty;
            if (length == 0 || !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) || code > 0x10FFFF)
            {
                throw Error(line, line.Indent + start + position, $"unknown escape '\\{escape}'");
            }

            builder.Append(char.ConvertFromUtf32(code));
            return position + 2 + length;
        }

        private NodeBuilder ParseBlockScalar(Line line, int valueStart, int indent, string name, string header)
        {
            var keep = header.Length > 1 && header[1] == '+';
            var strip = header.Length > 1 && header[1] == '-';
            var headerLength = keep || strip ? 2 : 1;
            if (header.Length > headerLength)
            {
                throw Error(line, line.Indent + valueStart + headerLength, "block scalar indentation indicators are not supported");
            }

            var contentLines = ReadBlockScalarLines(indent);
            var lastText = contentLines.FindLastIndex(content => content.Text.Length > 0);
            var node = new NodeBuilder(StructuredNodeKind.String, name, line.Number)
            {
                EndLine = lastText >= 0 ? contentLines[lastText].Number : line.Number,
            };
            var body = header[0] == '|' ? string.Join("\n", contentLines.ConvertAll(content => content.Text)) : Fold(contentLines);
            node.Value = Chomp(body, keep, strip);
            return node;
        }

        private List<(int Number, string Text)> ReadBlockScalarLines(int parentIndent)
        {
            var result = new List<(int Number, string Text)>();
            var contentIndent = -1;
            while (_index < _lines.Count)
            {
                var line = _lines[_index];
                var isBlank = line.Raw.Trim().Length == 0;
                if (!isBlank)
                {
                    if (contentIndent < 0)
                    {
                        contentIndent = line.Indent;
                    }

                    if (line.Indent < contentIndent || line.Indent <= parentIndent)
                    {
                        break;
                    }
                }

                var text = isBlank ? string.Empty : line.Raw.Substring(contentIndent);
                result.Add((line.Number, text));
                _index++;
            }

            return result;
        }

        /// <summary>Folds a &gt; block scalar: a break between two text lines becomes a space, blank lines become breaks.</summary>
        private static string Fold(List<(int Number, string Text)> lines)
        {
            var trailingBlanks = 0;
            while (trailingBlanks < lines.Count && lines[lines.Count - 1 - trailingBlanks].Text.Length == 0)
            {
                trailingBlanks++;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < lines.Count - trailingBlanks; i++)
            {
                var text = lines[i].Text;
                if (i > 0)
                {
                    builder.Append(Separator(lines[i - 1].Text, text));
                }

                builder.Append(text);
            }

            return builder.Append('\n', trailingBlanks).ToString();
        }

        private static string Separator(string previous, string current)
        {
            if (previous.Length == 0)
            {
                return "\n";
            }

            if (current.Length == 0)
            {
                return string.Empty;
            }

            // Lines indented more than the block keep their line breaks.
            var isMoreIndented = previous[0] == ' ' || current[0] == ' ';
            return isMoreIndented ? "\n" : " ";
        }

        private static string Chomp(string body, bool keep, bool strip)
        {
            var trimmed = body.TrimEnd('\n');
            if (trimmed.Length == 0)
            {
                return keep ? body + (body.Length > 0 ? "\n" : string.Empty) : string.Empty;
            }

            if (strip)
            {
                return trimmed;
            }

            return keep ? body + "\n" : trimmed + "\n";
        }

        private static bool IsSequenceItem(string content) =>
            content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        /// <summary>Finds "key:" at the start of the line's content, or returns null when the line is not a map entry.</summary>
        private MapKey? FindMapKey(Line line)
        {
            var content = line.Content;
            if (content.Length == 0 || content[0] is '[' or '{' || IsSequenceItem(content))
            {
                return null;
            }

            if (content == "?" || content.StartsWith("? ", StringComparison.Ordinal))
            {
                throw Error(line, line.Indent, "complex keys are not supported");
            }

            if (content[0] is '"' or '\'')
            {
                return FindQuotedKey(line);
            }

            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
                {
                    var name = content.Substring(0, i).TrimEnd();
                    if (name.Length > 0 && name[0] is '&' or '*' or '!')
                    {
                        ParseScalar(line, 0, name);
                    }

                    return new MapKey(name, i + 1);
                }
            }

            return null;
        }

        private MapKey? FindQuotedKey(Line line)
        {
            var content = line.Content;
            var end = 0;
            var name = ReadQuoted(line, 0, content, ref end);
            var colon = end;
            while (colon < content.Length && content[colon] == ' ')
            {
                colon++;
            }

            var isKey = colon < content.Length && content[colon] == ':' && (colon + 1 == content.Length || content[colon + 1] == ' ');
            return isKey ? new MapKey(name, colon + 1) : null;
        }

        private void CheckDepth(int depth)
        {
            if (depth > NodeBuilder.MaxDepth)
            {
                var line = _index < _lines.Count ? _lines[_index] : _lines[_lines.Count - 1];
                throw Error(line, line.Indent, "the YAML is nested too deeply");
            }
        }

        private static StructuredFormatException Error(Line line, int zeroBasedColumn, string reason) =>
            new(reason, line.Number, zeroBasedColumn + 1);

        /// <summary>Reads a flow collection ([a, b] or {a: 1}) that fits on one line.</summary>
        private sealed class FlowParser(Parser owner, Line line, int start, int depth)
        {
            private readonly string _text = line.Content;
            private int _position = start;

            public NodeBuilder ParseValue(string name) => ParseValue(name, depth);

            public void ExpectEnd()
            {
                SkipSpaces();
                if (_position < _text.Length)
                {
                    throw Error(line, line.Indent + _position, "unexpected text after the flow collection");
                }
            }

            private NodeBuilder ParseValue(string name, int level)
            {
                owner.CheckDepth(level);
                SkipSpaces();
                if (_position >= _text.Length)
                {
                    throw Error(line, line.Indent + _position, "flow collections spread over several lines are not supported");
                }

                return _text[_position] switch
                {
                    '[' => ParseSequence(name, level),
                    '{' => ParseMap(name, level),
                    _ => ParseScalar(name, "],}"),
                };
            }

            private NodeBuilder ParseSequence(string name, int level)
            {
                var node = new NodeBuilder(StructuredNodeKind.Array, name, line.Number);
                _position++;
                while (!TryClose(']'))
                {
                    node.Add(ParseValue(string.Empty, level + 1));
                    ExpectSeparator(']');
                }

                return node;
            }

            private NodeBuilder ParseMap(string name, int level)
            {
                var node = new NodeBuilder(StructuredNodeKind.Object, name, line.Number);
                var names = new HashSet<string>(StringComparer.Ordinal);
                _position++;
                while (!TryClose('}'))
                {
                    var keyColumn = _position;
                    var key = ParseScalar(string.Empty, ":,}").Value!;
                    if (!names.Add(key))
                    {
                        throw Error(line, line.Indent + keyColumn, $"duplicate key '{key}'");
                    }

                    SkipSpaces();
                    if (_position < _text.Length && _text[_position] == ':')
                    {
                        _position++;
                        SkipSpaces();
                        var hasValue = _position < _text.Length && _text[_position] is not (',' or '}');
                        node.Add(hasValue ? ParseValue(key, level + 1) : new NodeBuilder(StructuredNodeKind.Null, key, line.Number) { Value = NullValue });
                    }
                    else
                    {
                        node.Add(new NodeBuilder(StructuredNodeKind.Null, key, line.Number) { Value = NullValue });
                    }

                    ExpectSeparator('}');
                }

                return node;
            }

            private NodeBuilder ParseScalar(string name, string stops)
            {
                SkipSpaces();
                var begin = _position;
                if (_position < _text.Length && _text[_position] is '"' or '\'')
                {
                    var end = 0;
                    var quoted = owner.ReadQuoted(line, begin, _text.Substring(begin), ref end);
                    _position = begin + end;
                    return new NodeBuilder(StructuredNodeKind.String, name, line.Number) { Value = quoted };
                }

                while (_position < _text.Length && !IsStop(stops))
                {
                    _position++;
                }

                var plain = _text.Substring(begin, _position - begin).Trim();
                if (plain.Length == 0)
                {
                    throw Error(line, line.Indent + begin, "expected a value");
                }

                var (kind, value) = owner.ParseScalar(line, begin, plain);
                return new NodeBuilder(kind, name, line.Number) { Value = value };
            }

            private bool IsStop(string stops)
            {
                var current = _text[_position];
                if (current == ':')
                {
                    // A colon ends a flow key only when a space, separator or the end follows.
                    var next = _position + 1 < _text.Length ? _text[_position + 1] : ' ';
                    return stops.IndexOf(':') >= 0 && next is ' ' or ',' or '}' or ']';
                }

                return stops.IndexOf(current) >= 0 || current is '[' or '{';
            }

            private bool TryClose(char close)
            {
                SkipSpaces();
                if (_position < _text.Length && _text[_position] == close)
                {
                    _position++;
                    return true;
                }

                return false;
            }

            private void ExpectSeparator(char close)
            {
                SkipSpaces();
                if (_position < _text.Length && _text[_position] == ',')
                {
                    _position++;
                    return;
                }

                if (_position < _text.Length && _text[_position] == close)
                {
                    return;
                }

                throw Error(line, line.Indent + _position, _position >= _text.Length
                    ? "flow collections spread over several lines are not supported"
                    : $"expected ',' or '{close}'");
            }

            private void SkipSpaces()
            {
                while (_position < _text.Length && _text[_position] == ' ')
                {
                    _position++;
                }
            }
        }
    }
}
