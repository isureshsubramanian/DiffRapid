using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace DiffRapid.Core.Structured;

/// <summary>
/// Reads JSON (RFC 8259) into a structured tree. Object members are keyed by name; array items by position,
/// or by the value of a named property when an array key is given. Duplicate member names are an error.
/// </summary>
public static class JsonTreeReader
{
    /// <summary>Reads JSON text; the root node is the top-level value.</summary>
    /// <param name="text">The JSON text.</param>
    /// <param name="arrayKey">A property that identifies the objects inside arrays, or null to match items by position.</param>
    /// <param name="cancellationToken">Stops reading a large file.</param>
    /// <exception cref="StructuredFormatException">The text is not valid JSON.</exception>
    public static StructuredNode Read(string text, string? arrayKey = null, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text, cancellationToken);
        return parser.ParseDocument().Freeze(arrayKey, cancellationToken);
    }

    private sealed class Parser(string text, CancellationToken cancellationToken)
    {
        private int _position;
        private int _line = 1;
        private int _lineStart;

        public NodeBuilder ParseDocument()
        {
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                _position = 1;
                _lineStart = 1;
            }

            SkipWhitespace();
            if (_position >= text.Length)
            {
                throw Error("the file is empty");
            }

            var root = ParseValue(string.Empty, _line, 1);
            SkipWhitespace();
            if (_position < text.Length)
            {
                throw Error("unexpected text after the JSON value");
            }

            return root;
        }

        private NodeBuilder ParseValue(string name, int startLine, int depth)
        {
            if (depth > NodeBuilder.MaxDepth)
            {
                throw Error("the JSON is nested too deeply");
            }

            if (_position >= text.Length)
            {
                throw Error("unexpected end of the file");
            }

            var current = text[_position];
            switch (current)
            {
                case '{':
                    return ParseObject(name, startLine, depth);
                case '[':
                    return ParseArray(name, startLine, depth);
                case '"':
                    return Scalar(name, startLine, StructuredNodeKind.String, ParseString());
                case 't':
                    return Literal(name, startLine, "true", StructuredNodeKind.Boolean);
                case 'f':
                    return Literal(name, startLine, "false", StructuredNodeKind.Boolean);
                case 'n':
                    return Literal(name, startLine, "null", StructuredNodeKind.Null);
                default:
                    if (current == '-' || (current >= '0' && current <= '9'))
                    {
                        return Scalar(name, startLine, StructuredNodeKind.Number, ParseNumber());
                    }

                    throw Error($"unexpected character '{current}'");
            }
        }

        private NodeBuilder ParseObject(string name, int startLine, int depth)
        {
            var node = new NodeBuilder(StructuredNodeKind.Object, name, startLine);
            var names = new HashSet<string>(StringComparer.Ordinal);
            _position++;
            SkipWhitespace();
            if (TryConsume('}'))
            {
                node.EndLine = _line;
                return node;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SkipWhitespace();
                if (_position >= text.Length || text[_position] != '"')
                {
                    throw Error("expected a property name in double quotes");
                }

                var keyLine = _line;
                var keyColumn = Column;
                var key = ParseString();
                if (!names.Add(key))
                {
                    throw new StructuredFormatException($"duplicate property '{key}'", keyLine, keyColumn);
                }

                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                node.Add(ParseValue(key, keyLine, depth + 1));
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    node.EndLine = _line;
                    return node;
                }

                Expect(',');
            }
        }

        private NodeBuilder ParseArray(string name, int startLine, int depth)
        {
            var node = new NodeBuilder(StructuredNodeKind.Array, name, startLine);
            _position++;
            SkipWhitespace();
            if (TryConsume(']'))
            {
                node.EndLine = _line;
                return node;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SkipWhitespace();
                node.Add(ParseValue(string.Empty, _line, depth + 1));
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    node.EndLine = _line;
                    return node;
                }

                Expect(',');
            }
        }

        private NodeBuilder Scalar(string name, int startLine, StructuredNodeKind kind, string value) =>
            new(kind, name, startLine) { Value = value, EndLine = _line };

        private NodeBuilder Literal(string name, int startLine, string literal, StructuredNodeKind kind)
        {
            if (string.CompareOrdinal(text, _position, literal, 0, literal.Length) != 0)
            {
                throw Error("unexpected character '" + text[_position] + "'");
            }

            _position += literal.Length;
            return Scalar(name, startLine, kind, literal);
        }

        private string ParseString()
        {
            _position++;
            var builder = new StringBuilder();
            while (true)
            {
                if (_position >= text.Length)
                {
                    throw Error("the string is not closed");
                }

                var current = text[_position];
                if (current == '"')
                {
                    _position++;
                    return builder.ToString();
                }

                if (current < ' ')
                {
                    throw Error("control character in a string");
                }

                if (current == '\\')
                {
                    builder.Append(ParseEscape());
                    continue;
                }

                builder.Append(current);
                _position++;
            }
        }

        private char ParseEscape()
        {
            _position++;
            if (_position >= text.Length)
            {
                throw Error("the string is not closed");
            }

            var escape = text[_position++];
            switch (escape)
            {
                case '"':
                case '\\':
                case '/':
                    return escape;
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'u':
                    var digits = _position + 4 <= text.Length ? text.Substring(_position, 4) : string.Empty;
                    if (!ushort.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        throw Error("\\u needs four hex digits");
                    }

                    _position += 4;
                    return (char)code;
                default:
                    _position--;
                    throw Error($"unknown escape '\\{escape}'");
            }
        }

        private string ParseNumber()
        {
            var start = _position;
            TryConsume('-');
            if (TryConsume('0'))
            {
                RequireNoDigit();
            }
            else
            {
                RequireDigits();
            }

            if (TryConsume('.'))
            {
                RequireDigits();
            }

            if (TryConsume('e') || TryConsume('E'))
            {
                if (!TryConsume('+'))
                {
                    TryConsume('-');
                }

                RequireDigits();
            }

            return text.Substring(start, _position - start);
        }

        private void RequireDigits()
        {
            var start = _position;
            while (_position < text.Length && text[_position] >= '0' && text[_position] <= '9')
            {
                _position++;
            }

            if (_position == start)
            {
                throw Error("invalid number");
            }
        }

        private void RequireNoDigit()
        {
            if (_position < text.Length && text[_position] >= '0' && text[_position] <= '9')
            {
                throw Error("numbers cannot have leading zeros");
            }
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length)
            {
                var current = text[_position];
                if (current == '\n' || (current == '\r' && (_position + 1 >= text.Length || text[_position + 1] != '\n')))
                {
                    _position++;
                    _line++;
                    _lineStart = _position;
                }
                else if (current == ' ' || current == '\t' || current == '\r')
                {
                    _position++;
                }
                else
                {
                    return;
                }
            }
        }

        private bool TryConsume(char expected)
        {
            if (_position < text.Length && text[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
            {
                var found = _position < text.Length ? "'" + text[_position] + "'" : "the end of the file";
                throw Error($"expected '{expected}' but found {found}");
            }
        }

        private int Column => _position - _lineStart + 1;

        private StructuredFormatException Error(string reason) => new(reason, _line, Column);
    }
}
