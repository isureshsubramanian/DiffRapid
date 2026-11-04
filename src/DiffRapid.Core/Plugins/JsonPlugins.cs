using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// PrettifyJson and JsonQuery. JsonQuery understands a small subset of jq paths: <c>.</c>,
/// <c>.name</c>, <c>."quoted name"</c>, <c>.[2]</c> (negative counts from the end), <c>.[]</c> (every
/// array item or object value), chains such as <c>.items[].name</c>, and a trailing <c>keys</c>
/// (sorted object keys, or array indexes). A missing key gives <c>null</c>, as in jq.
/// </summary>
internal static class JsonPlugins
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    public static string Prettify(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("PrettifyJson", context.Arguments, flags: ["sort"], keys: ["indent"]);
        arguments.ExpectAtMost(0);
        var indent = arguments.PositiveInt("indent", 2);
        using var document = ParseDocument(text, "PrettifyJson");
        return Write(writer => WriteElement(writer, document.RootElement, arguments.Has("sort")), indent) + "\n";
    }

    public static string Query(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("JsonQuery", context.Arguments, flags: ["keys"]);
        var path = arguments.Positionals.Count == 0 ? "." : arguments.Positionals[0];
        arguments.ExpectAtMost(1);
        var keys = arguments.Has("keys") || path.Equals("keys", StringComparison.Ordinal);
        if (path.Equals("keys", StringComparison.Ordinal))
        {
            path = ".";
        }

        var segments = ParsePath(path);
        using var document = ParseDocument(text, "JsonQuery");
        IEnumerable<JsonElement?> results = [document.RootElement];
        foreach (var segment in segments)
        {
            results = results.SelectMany(element => segment.Apply(element)).ToList();
        }

        var output = new StringBuilder();
        foreach (var result in results)
        {
            var written = keys
                ? Write(writer => WriteKeys(writer, result), 2)
                : Write(writer => WriteValue(writer, result), 2);
            output.Append(written).Append('\n');
        }

        return output.ToString();
    }

    private static JsonDocument ParseDocument(string text, string pluginName)
    {
        try
        {
            return JsonDocument.Parse(text.TrimStart((char)0xFEFF), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new PluginException($"{pluginName}: the text is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static string Write(Action<Utf8JsonWriter> write, int indent)
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = true,
            IndentSize = indent,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = false,
        };
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, bool sortKeys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToList();
                var ordered = sortKeys ? properties.OrderBy(property => property.Name, StringComparer.Ordinal) : (IEnumerable<JsonProperty>)properties;
                foreach (var property in ordered)
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, sortKeys);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, sortKeys);
                }

                writer.WriteEndArray();
                break;
            default:
                // Numbers keep their original spelling.
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement? element)
    {
        if (element is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteElement(writer, element.Value, sortKeys: false);
        }
    }

    private static void WriteKeys(Utf8JsonWriter writer, JsonElement? element)
    {
        writer.WriteStartArray();
        if (element?.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in element.Value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal))
            {
                writer.WriteStringValue(name);
            }
        }
        else if (element?.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < element.Value.GetArrayLength(); i++)
            {
                writer.WriteNumberValue(i);
            }
        }
        else
        {
            throw new PluginException($"JsonQuery: {Describe(element)} has no keys.");
        }

        writer.WriteEndArray();
    }

    private static List<PathSegment> ParsePath(string path)
    {
        if (path.Length == 0 || path[0] != '.')
        {
            throw new ArgumentException($"JsonQuery: the path '{path}' must start with '.'.");
        }

        var segments = new List<PathSegment>();
        var position = 0;
        while (position < path.Length)
        {
            if (path[position] == '.')
            {
                position++;
                if (position >= path.Length || path[position] == '[')
                {
                    continue;
                }

                segments.Add(PathSegment.Key(ReadKey(path, ref position)));
            }
            else if (path[position] == '[')
            {
                segments.Add(ReadBracket(path, ref position));
            }
            else
            {
                throw InvalidPath(path, position);
            }
        }

        return segments;
    }

    private static string ReadKey(string path, ref int position)
    {
        if (path[position] == '"')
        {
            var close = path.IndexOf('"', position + 1);
            if (close < 0)
            {
                throw InvalidPath(path, position);
            }

            var quoted = path.Substring(position + 1, close - position - 1);
            position = close + 1;
            return quoted;
        }

        var start = position;
        while (position < path.Length && (char.IsLetterOrDigit(path[position]) || path[position] is '_' or '-' or '$'))
        {
            position++;
        }

        if (position == start)
        {
            throw InvalidPath(path, position);
        }

        return path.Substring(start, position - start);
    }

    private static PathSegment ReadBracket(string path, ref int position)
    {
        var close = path.IndexOf(']', position);
        if (close < 0)
        {
            throw InvalidPath(path, position);
        }

        var inside = path.Substring(position + 1, close - position - 1).Trim();
        position = close + 1;
        if (inside.Length == 0)
        {
            return PathSegment.Iterate();
        }

        if (inside.Length >= 2 && inside[0] == '"' && inside[inside.Length - 1] == '"')
        {
            return PathSegment.Key(inside.Substring(1, inside.Length - 2));
        }

        if (!int.TryParse(inside, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException($"JsonQuery: '[{inside}]' is not an index; use [n], [] or [\"key\"].");
        }

        return PathSegment.Index(index);
    }

    private static ArgumentException InvalidPath(string path, int position) =>
        new($"JsonQuery: the path '{path}' is not valid at position {position + 1}.");

    private static string Describe(JsonElement? element) => element is null ? "null" : element.Value.ValueKind.ToString().ToLowerInvariant();

    private sealed class PathSegment
    {
        private readonly Func<JsonElement?, IEnumerable<JsonElement?>> _apply;

        private PathSegment(Func<JsonElement?, IEnumerable<JsonElement?>> apply) => _apply = apply;

        public IEnumerable<JsonElement?> Apply(JsonElement? element) => _apply(element);

        public static PathSegment Key(string name) => new(element => [GetProperty(element, name)]);

        public static PathSegment Index(int index) => new(element => [GetItem(element, index)]);

        public static PathSegment Iterate() => new(Iterate);

        private static JsonElement? GetProperty(JsonElement? element, string name)
        {
            if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.Value.ValueKind != JsonValueKind.Object)
            {
                throw new PluginException($"JsonQuery: cannot read key '{name}' of {Describe(element)}.");
            }

            return element.Value.TryGetProperty(name, out var value) ? value : null;
        }

        private static JsonElement? GetItem(JsonElement? element, int index)
        {
            if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.Value.ValueKind != JsonValueKind.Array)
            {
                throw new PluginException($"JsonQuery: cannot index {Describe(element)} with [{index}].");
            }

            var length = element.Value.GetArrayLength();
            var actual = index < 0 ? length + index : index;
            return actual >= 0 && actual < length ? element.Value[actual] : null;
        }

        private static IEnumerable<JsonElement?> Iterate(JsonElement? element)
        {
            if (element?.ValueKind == JsonValueKind.Array)
            {
                return element.Value.EnumerateArray().Select(item => (JsonElement?)item).ToList();
            }

            if (element?.ValueKind == JsonValueKind.Object)
            {
                return element.Value.EnumerateObject().Select(property => (JsonElement?)property.Value).ToList();
            }

            throw new PluginException($"JsonQuery: cannot iterate over {Describe(element)}.");
        }
    }
}
