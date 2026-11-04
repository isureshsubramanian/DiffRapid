# DiffRapid MCP server

`diffrapid-mcp` exposes read-only comparisons over MCP stdio using the official
[`ModelContextProtocol` C# SDK](https://www.nuget.org/packages/ModelContextProtocol/2.3.1)
2.3.1 (Apache-2.0). It targets .NET 10. Only newline-delimited protocol messages
go to stdout; startup errors go to stderr. The client starts and stops the server.

Build or publish from the repository root:

```powershell
dotnet build src/DiffRapid.Mcp
dotnet publish src/DiffRapid.Mcp -c Release -o artifacts/mcp
```

Register the absolute path to `artifacts/mcp/diffrapid-mcp.exe` on Windows, or
`artifacts/mcp/diffrapid-mcp` on Linux/macOS. Keep all published files together.
A framework-dependent deployment requires the .NET 10 runtime. You can also use
`dotnet` as the command with the absolute path to `diffrapid-mcp.dll` as its argument.
Relative comparison paths resolve against the server's working directory; absolute
paths avoid differences between client working directories.

## Claude Code

Use a project-scoped registration:

```powershell
claude mcp add --transport stdio --scope project diffrapid -- C:/Tools/DiffRapid/diffrapid-mcp.exe
claude mcp list
```

Alternatively, merge this registration into the workspace's `.mcp.json`:

```json
{
  "mcpServers": {
    "diffrapid": {
      "command": "C:/Tools/DiffRapid/diffrapid-mcp.exe",
      "args": []
    }
  }
}
```

See the [Claude Code MCP configuration documentation](https://code.claude.com/docs/en/mcp).

## Visual Studio and Copilot

Visual Studio's `.mcp.json` uses a `servers` object. Place this in the solution
directory, open Copilot Chat in Agent mode, and enable the DiffRapid tools:

```json
{
  "servers": {
    "diffrapid": {
      "type": "stdio",
      "command": "C:/Tools/DiffRapid/diffrapid-mcp.exe",
      "args": []
    }
  }
}
```

The same `servers` form works in VS Code's `.vscode/mcp.json`. See the
[Visual Studio MCP documentation](https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers)
and [VS Code MCP configuration reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).

## Other clients and CLI registration

Cursor's `.cursor/mcp.json` and Claude Desktop's MCP configuration accept the
`mcpServers` form above. For other clients, add a local stdio server with the
executable as its command and no arguments.

`diffrapid mcp` prints the `mcpServers` registration JSON. If the MCP executable
is beside the CLI executable, the command contains its absolute path; otherwise
it uses `diffrapid-mcp.exe` (Windows) or `diffrapid-mcp` from PATH. The CLI does
not start the server and does not depend on the MCP project. For Visual Studio,
rename the printed outer `mcpServers` key to `servers` and add `"type": "stdio"`.

## Tools and results

Every tool advertises its input JSON schema and read-only annotations through
`tools/list`. Successful results contain the same JSON in `structuredContent`
and a text content block, for clients that only consume text.

| Tool | Inputs and result |
| --- | --- |
| `compare_files` | `left`, `right`: local files. Extracts docx/xlsx/pptx text. Returns file counts, hunks with one-based line ranges and source lines, and `unifiedText`. |
| `compare_text` | `left_text`, `right_text`: literal strings, including empty content. Same options and result as file comparison. |
| `compare_folders` | `left`, `right`: directories. Optional `method`, `recursive` (true), `filter` (include glob), `include_identical` (false), `max_entries` (100). Returns complete counts and selected items. |
| `merge_three_way` | `base`, `left`, `right`: text file paths. Returns `mergedText` with diff3 conflict markers, `conflictCount`, and conflict line ranges. |
| `compare_structure` | `left`, `right`: JSON, XML, YAML, MSBuild or resx paths. The left extension selects the format. Returns key paths, old/new values, and line spans. YAML supports Core's block-style subset. |
| `compare_api` | `left`, `right`: two .NET dll/exe files or two nupkg files. Reads metadata without executing assemblies. Returns API and metadata changes and breaking-change flags. |
| `semantic_diff` | `left`, `right`: C# paths. Returns member-level changes, signatures, one-based line ranges, and syntax-error flags for best-effort parses. |
| `create_patch` | `left`, `right`: two files or two directories. Returns unified `patch` text with three context lines. Binary folder changes are described as binary differences. |

Text comparisons accept `ignore_whitespace`, `ignore_case`, `ignore_blank_lines`
(booleans, default false), `algorithm` (`default`, `minimal`, `patience`,
`histogram`, `none`; default `histogram`), `context` (default 3), and `max_hunks`
(default 100). `ignore_whitespace` removes all whitespace from comparison keys.
Folder methods are `full`, `quick`, `binary`, `date`, `date-size`, `size`,
`existence`, and `hash`; default `full`. Default Core folder exclusions apply,
and symbolic links are not followed.

File/folder reports follow the version 1 field names and meanings in
`DiffRapid.Core/Output/JsonReportWriter.cs`, with additive `truncated` and unified
text fields. Counts describe the complete comparison even when details are
limited. Empty line ranges use the following one-based line number. Structured
spans have inclusive `startLine` and `endLine` values.

`context`, `max_hunks`, and `max_entries` must be between 0 and 1000. Zero detail
limits return counts only. Responses bound source text and unified/patch text
to 65,536 characters each, lists to at most 1000 entries, and input files to
64 MiB each. Literal comparison text is limited to 64 Mi characters. A response
sets `truncated: true` whenever content or entries were omitted. Truncated merge
text and patches are incomplete previews. Use narrower files or folders when a
complete large result is needed. No tool writes source files, merge results,
patches, or reports.

Missing paths, unsupported inputs, malformed documents and invalid options return
`isError: true` with a readable message. MCP cancellation is forwarded to the
comparison engines; existing synchronous readers and renderers observe it at
operation boundaries. There is no network transport or path allowlist: tools can
read files accessible to the server process.
