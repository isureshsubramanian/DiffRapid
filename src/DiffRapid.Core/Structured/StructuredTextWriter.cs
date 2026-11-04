using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DiffRapid.Core.Structured;

/// <summary>
/// Writes a structured tree back as text with children in key order and a fixed layout, so that two files
/// written this way line up in an ordinary text diff. Comments, the XML declaration and the original spacing
/// are not kept; the output uses "\n" line endings and two-space indentation.
/// </summary>
public static class StructuredTextWriter
{
    /// <summary>Writes the tree in its format's canonical, key-ordered layout.</summary>
    /// <param name="root">A tree from one of the readers.</param>
    /// <param name="format">The format the tree was read as; XML, MSBuild and .resx trees are written as XML.</param>
    /// <exception cref="ArgumentException">The tree holds nodes the format cannot express.</exception>
    public static string Write(StructuredNode root, StructuredFormat format)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var builder = new StringBuilder();
        switch (format)
        {
            case StructuredFormat.Xml:
            case StructuredFormat.MSBuild:
            case StructuredFormat.Resx:
                WriteXml(builder, root, 0);
                break;
            case StructuredFormat.Json:
                WriteJson(builder, root, 0);
                builder.Append('\n');
                break;
            case StructuredFormat.Yaml:
                WriteYaml(builder, root);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Choose the format the tree was read as.");
        }

        return builder.ToString();
    }

    private static IEnumerable<StructuredNode> KeyOrder(StructuredNode node)
    {
        // Items matched by position keep their order; everything else is sorted by key.
        if (node.Children.Any(child => child.IsKeyedByIndex))
        {
            return node.Children;
        }

        return node.Children.OrderBy(child => child.Key, StringComparer.Ordinal);
    }

    private static void WriteXml(StringBuilder builder, StructuredNode node, int indent)
    {
        switch (node.Kind)
        {
            case StructuredNodeKind.Document:
                foreach (var child in KeyOrder(node))
                {
                    WriteXml(builder, child, indent);
                }

                break;
            case StructuredNodeKind.Element:
                WriteElement(builder, node, indent);
                break;
            case StructuredNodeKind.Text:
                builder.Append(' ', indent).Append(EscapeXml(node.Value!.Trim(), false)).Append('\n');
                break;
            default:
                throw new ArgumentException($"A {node.Kind} node at {node.Path} cannot be written as XML.", nameof(node));
        }
    }

    private static void WriteElement(StringBuilder builder, StructuredNode element, int indent)
    {
        builder.Append(' ', indent).Append('<').Append(element.Name);
        var attributes = element.Children.Where(child => child.Kind == StructuredNodeKind.Attribute)
            .OrderBy(child => child.Name == element.IdentityAttribute ? 0 : 1)
            .ThenBy(child => child.Name, StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            builder.Append(' ').Append(attribute.Name).Append("=\"").Append(EscapeXml(attribute.Value ?? string.Empty, true)).Append('"');
        }

        var content = KeyOrder(element).Where(child => child.Kind != StructuredNodeKind.Attribute).ToList();
        if (content.Count == 0)
        {
            if (string.IsNullOrEmpty(element.Value))
            {
                builder.Append(" />\n");
                return;
            }

            builder.Append('>').Append(EscapeXml(element.Value!, false)).Append("</").Append(element.Name).Append(">\n");
            return;
        }

        builder.Append(">\n");
        foreach (var child in content)
        {
            WriteXml(builder, child, indent + 2);
        }

        builder.Append(' ', indent).Append("</").Append(element.Name).Append(">\n");
    }

    private static string EscapeXml(string text, bool attribute)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var escaped = character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' when attribute => "&quot;",
                '\n' when attribute => "&#10;",
                '\r' => "&#13;",
                '\t' when attribute => "&#9;",
                _ => null,
            };
            if (escaped is null)
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(escaped);
            }
        }

        return builder.ToString();
    }

    private static void WriteJson(StringBuilder builder, StructuredNode node, int indent)
    {
        switch (node.Kind)
        {
            case StructuredNodeKind.Object:
            case StructuredNodeKind.Array:
                WriteJsonContainer(builder, node, indent);
                break;
            case StructuredNodeKind.String:
                AppendJsonString(builder, node.Value ?? string.Empty);
                break;
            case StructuredNodeKind.Number:
                builder.Append(node.Value);
                break;
            case StructuredNodeKind.Boolean:
                builder.Append(node.Value!.ToLowerInvariant());
                break;
            case StructuredNodeKind.Null:
                builder.Append("null");
                break;
            default:
                throw new ArgumentException($"A {node.Kind} node at {node.Path} cannot be written as JSON.", nameof(node));
        }
    }

    private static void WriteJsonContainer(StringBuilder builder, StructuredNode node, int indent)
    {
        var isObject = node.Kind == StructuredNodeKind.Object;
        if (node.Children.Count == 0)
        {
            builder.Append(isObject ? "{}" : "[]");
            return;
        }

        builder.Append(isObject ? "{\n" : "[\n");
        var first = true;
        foreach (var child in KeyOrder(node))
        {
            if (!first)
            {
                builder.Append(",\n");
            }

            first = false;
            builder.Append(' ', indent + 2);
            if (isObject)
            {
                AppendJsonString(builder, child.Name);
                builder.Append(": ");
            }

            WriteJson(builder, child, indent + 2);
        }

        builder.Append('\n').Append(' ', indent).Append(isObject ? '}' : ']');
    }

    private static void AppendJsonString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var character in text)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static void WriteYaml(StringBuilder builder, StructuredNode root)
    {
        var lines = new List<string>();
        if (IsYamlContainer(root) && root.Children.Count > 0)
        {
            AppendYamlBlock(lines, root, 0);
        }
        else
        {
            lines.Add(YamlInline(root));
        }

        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    /// <summary>Adds the lines of a non-empty map or sequence, indented by the given number of spaces.</summary>
    private static void AppendYamlBlock(List<string> lines, StructuredNode node, int indent)
    {
        var padding = new string(' ', indent);
        foreach (var child in KeyOrder(node))
        {
            var lead = node.Kind == StructuredNodeKind.Object ? padding + YamlScalar(child.Name, StructuredNodeKind.String) + ":" : padding + "-";
            if (!IsYamlContainer(child) || child.Children.Count == 0)
            {
                lines.Add(lead + " " + YamlInline(child));
                continue;
            }

            if (node.Kind == StructuredNodeKind.Array)
            {
                // "- " followed by the item's first line; the rest of the item lines up after the dash.
                var start = lines.Count;
                AppendYamlBlock(lines, child, indent + 2);
                lines[start] = padding + "- " + lines[start].Substring(indent + 2);
                continue;
            }

            lines.Add(lead);
            AppendYamlBlock(lines, child, indent + 2);
        }
    }

    private static bool IsYamlContainer(StructuredNode node) =>
        node.Kind is StructuredNodeKind.Object or StructuredNodeKind.Array;

    private static string YamlInline(StructuredNode node) =>
        node.Kind switch
        {
            StructuredNodeKind.Object => "{}",
            StructuredNodeKind.Array => "[]",
            StructuredNodeKind.String or StructuredNodeKind.Number or StructuredNodeKind.Boolean or StructuredNodeKind.Null =>
                YamlScalar(node.Value ?? string.Empty, node.Kind),
            _ => throw new ArgumentException($"A {node.Kind} node at {node.Path} cannot be written as YAML.", nameof(node)),
        };

    private static string YamlScalar(string value, StructuredNodeKind kind)
    {
        if (kind == StructuredNodeKind.Null)
        {
            return "null";
        }

        if (kind != StructuredNodeKind.String)
        {
            return value;
        }

        return NeedsQuotes(value) ? QuoteYaml(value) : value;
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0 || value.Trim().Length != value.Length || YamlTreeReader.PlainKind(value) != StructuredNodeKind.String)
        {
            return true;
        }

        if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) >= 0)
        {
            return true;
        }

        return value.Contains(": ") || value.Contains(" #") || value.EndsWith(":", StringComparison.Ordinal)
            || value.Any(character => character < ' ' || character == '\u007f');
    }

    private static string QuoteYaml(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (character < ' ' || character == '\u007f')
                    {
                        builder.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
