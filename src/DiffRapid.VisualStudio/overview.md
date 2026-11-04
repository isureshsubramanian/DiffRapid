# DiffRapid

**Fast, thorough compare and merge without leaving Visual Studio.**

DiffRapid shows differences in the real Visual Studio editor, so syntax highlighting, IntelliSense and
Go To Definition keep working while you compare. It re-compares as you type and handles large files and
folders quickly: a 10 MB file pair in under half a second, 200,000 files in about a second.

> **Preview.** DiffRapid is in active development. Please report problems and ideas on
> [GitHub](https://github.com/isureshsubramanian/DiffRapid/issues).

## Compare anything

| | |
|---|---|
| **Text** | Side-by-side panes with inline word and character changes, moved blocks, an overview bar, and ignore options for whitespace, case, blank lines, line endings, numbers and regular expressions |
| **Merge** | Three-way merge with an editable result, per-conflict choices and Auto-resolve for safe conflicts |
| **Folders** | Tree view with summary cards, rename and move detection, filters, three-way folders, synchronization and HTML/CSV reports |
| **Hex** | Binary files byte by byte, with alignment of inserted and deleted bytes |
| **Images** | Side by side, highlight, XOR, blend, swipe and subtract |
| **Tables** | CSV, TSV and Excel sheets matched by key columns, with numeric tolerance |
| **Documents** | Word, Excel and PowerPoint compared as text |
| **Structure** | JSON, XML, YAML, MSBuild and .resx compared by keys, ignoring formatting and order |
| **C# and .NET** | Member-by-member C# comparison through Roslyn, and the public API of assemblies and NuGet packages with breaking changes marked |

## Built for daily work

- **Compare from anywhere:** two files in Solution Explorer, a file with the clipboard, with its saved version,
  with an earlier Git or TFVC version, or with its local history.
- **Local history:** every save is kept, so you can go back even outside source control.
- **Plugins:** unpack archives and normalize text (sort JSON keys, sort lines, select JSON paths) before comparing.
- **Patches and reports:** create and apply patches with a preview, export comparisons to HTML or Markdown.
- **Keyboard:** F8 / Shift+F8 for the next and previous difference, Ctrl+F8 / Ctrl+Shift+F8 to copy across.

## AI, only when you ask

Explain a diff or get a proposal for a merge conflict from an AI service you configure: OpenAI, Azure OpenAI,
or a local model through Ollama or LM Studio. It is off until you set it up in
**Tools > Options > DiffRapid > AI**, asks before the first request, masks passwords, keys and connection strings,
and never changes your files: proposals are applied only when you choose *Use proposal*.

## Getting started

1. Select two files in Solution Explorer, right-click and choose **Compare Selected with DiffRapid**.
2. Or use **Tools > DiffRapid: Compare Files...**, **Compare Folders...** or **Merge Files...**.
3. Adjust behaviour in **Tools > Options > DiffRapid** and colours in
   **Tools > Options > Environment > Fonts and Colors** (items starting with "DiffRapid").

DiffRapid also ships a command line (`diffrapid`) and an MCP server for AI agents; see the
[repository](https://github.com/isureshsubramanian/DiffRapid) for both.

Free and open source under the [Apache License 2.0](https://github.com/isureshsubramanian/DiffRapid/blob/main/LICENSE).
