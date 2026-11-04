using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiffRapid.Mcp;

/// <summary>Defines the read-only comparison tools served over MCP.</summary>
public static class ComparisonServer
{
    private static readonly IReadOnlyList<Tool> Tools = CreateTools();
    private static readonly JsonSerializerOptions ReportOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Creates the protocol handlers, also usable with an in-memory stream transport.</summary>
    public static McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new Implementation { Name = "diffrapid-mcp", Version = "1.0.0" },
        ServerInstructions = "Read-only comparisons. Paths are local to the server. Line numbers are one-based. Check truncated before treating output as complete.",
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ListToolsResult { Tools = Tools.ToList() });
            },
            CallToolHandler = (request, cancellationToken) => new ValueTask<CallToolResult>(Task.Run(() =>
                CallTool(request.Params?.Name ?? "", JsonSerializer.SerializeToElement(request.Params?.Arguments), cancellationToken), cancellationToken)),
        },
    };

    /// <summary>Calls a comparison tool; invalid inputs become readable MCP error results.</summary>
    public static CallToolResult CallTool(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = Tools.FirstOrDefault(candidate => candidate.Name == name)
                ?? throw new ArgumentException($"Unknown tool '{name}'.");
            ValidateArguments(arguments, tool.InputSchema);
            var report = ComparisonTools.Run(name, arguments, cancellationToken);
            var structured = JsonSerializer.SerializeToElement(report, ReportOptions);
            return new CallToolResult
            {
                IsError = false,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{name}: {exception.Message}" }],
            };
        }
    }

    private static void ValidateArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        var properties = schema.GetProperty("properties");
        foreach (var argument in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(argument.Name, out var property))
            {
                throw new ArgumentException($"Unknown argument '{argument.Name}'.");
            }

            var type = property.GetProperty("type").GetString();
            var valid = type switch
            {
                "string" => argument.Value.ValueKind == JsonValueKind.String,
                "boolean" => argument.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => argument.Value.ValueKind == JsonValueKind.Number && argument.Value.TryGetInt32(out _),
                _ => false,
            };
            if (!valid)
            {
                throw new ArgumentException($"'{argument.Name}' must be a {type}.");
            }
        }

        foreach (var required in schema.GetProperty("required").EnumerateArray())
        {
            if (!arguments.TryGetProperty(required.GetString()!, out _))
            {
                throw new ArgumentException($"Missing required argument '{required.GetString()}'.");
            }
        }
    }

    private static IReadOnlyList<Tool> CreateTools()
    {
        var tools = new List<Tool>();
        foreach (var name in new[] { "compare_files", "compare_text" })
        {
            var text = name == "compare_text";
            var properties = PairProperties(text ? "left_text" : "left", text ? "right_text" : "right", text ? "Literal text; empty is allowed." : "Existing local file path. Office documents are extracted as text.");
            properties["ignore_whitespace"] = Property("boolean", "Ignore all whitespace when true; default false.", false);
            properties["ignore_case"] = Property("boolean", "Ignore letter case; default false.", false);
            properties["ignore_blank_lines"] = Property("boolean", "Classify changes containing only blank lines as ignored; default false.", false);
            properties["algorithm"] = Property("string", "Line matching algorithm.", "histogram", ["default", "minimal", "patience", "histogram", "none"]);
            properties["context"] = Property("integer", "Unified diff context lines.", 3);
            properties["max_hunks"] = Property("integer", "Maximum returned differing blocks. Counts describe the full comparison.", 100);
            tools.Add(CreateTool(name, "Compare " + (text ? "literal text" : "local files") + ". Returns version 1 file report fields, complete counts, differing line ranges, source lines and unified text. Large output sets truncated=true.", properties, text ? ["left_text", "right_text"] : ["left", "right"]));
        }

        var folders = PairProperties("left", "right", "Existing local directory path.");
        folders["method"] = Property("string", "Folder comparison method.", "full", ["full", "quick", "binary", "date", "date-size", "size", "existence", "hash"]);
        folders["recursive"] = Property("boolean", "Descend into subdirectories. Symbolic links are not followed.", true);
        folders["filter"] = Property("string", "Optional include glob such as *.cs or src/**. Uses Core folder masks and default exclusions.");
        folders["include_identical"] = Property("boolean", "Return identical entries too. Counts always cover all entries.", false);
        folders["max_entries"] = Property("integer", "Maximum returned entries. Large output sets truncated=true.", 100);
        tools.Add(CreateTool("compare_folders", "Compare directory trees. Returns version 1 folder report fields with complete counts and bounded items. No files are written.", folders, ["left", "right"]));

        var merge = PairProperties("left", "right", "Existing local text file path.");
        merge["base"] = Property("string", "Existing common ancestor text file path.");
        tools.Add(CreateTool("merge_three_way", "Preview a three-way merge of three files. Returns mergedText with conflict markers and conflicts with one-based ranges. Never writes the merge. Large output sets truncated=true.", merge, ["base", "left", "right"]));
        tools.Add(CreateTool("compare_structure", "Compare local JSON, XML, YAML, MSBuild or resx files by keys. Returns paths, values and line spans of structural differences, ignoring key order. YAML uses Core's supported block subset.", PairProperties("left", "right", "Existing structured file path; left extension selects the format."), ["left", "right"]));
        tools.Add(CreateTool("compare_api", "Compare declared public/protected .NET APIs without executing assemblies. Both inputs must be .dll/.exe assemblies, or both .nupkg packages. Returns API and metadata changes, marking potentially breaking changes.", PairProperties("left", "right", "Existing .dll, .exe or .nupkg path."), ["left", "right"]));
        tools.Add(CreateTool("semantic_diff", "Compare C# files member by member. Returns changed/added/removed/moved/renamed members, signatures and one-based line ranges, plus parse-error flags. Large output sets truncated=true.", PairProperties("left", "right", "Existing C# source file path."), ["left", "right"]));
        tools.Add(CreateTool("create_patch", "Create unified diff text for two files or two directory trees, with three context lines. No patch or source files are written. Binary folder changes are described. Large output sets truncated=true; a truncated patch is incomplete.", PairProperties("left", "right", "Existing file or directory path; both sides must have the same type."), ["left", "right"]));
        return tools.AsReadOnly();
    }

    private static Dictionary<string, object> PairProperties(string left, string right, string description) => new()
    {
        [left] = Property("string", description),
        [right] = Property("string", description),
    };

    private static object Property(string type, string description, object? defaultValue = null, string[]? choices = null)
    {
        var property = new Dictionary<string, object> { ["type"] = type, ["description"] = description };
        if (defaultValue is not null)
        {
            property["default"] = defaultValue;
        }

        if (choices is not null)
        {
            property["enum"] = choices;
        }

        if (type == "integer")
        {
            property["minimum"] = 0;
            property["maximum"] = 1000;
        }

        return property;
    }

    private static Tool CreateTool(string name, string description, Dictionary<string, object> properties, string[] required) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    };
}
