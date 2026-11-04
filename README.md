<div align="center">

# DiffRapid

**Fast, thorough compare and merge for Visual Studio 2026, with a matching command line and an MCP server for AI agents.**

[![Visual Studio Marketplace](https://img.shields.io/badge/Visual%20Studio%20Marketplace-DiffRapid-5C2D91?logo=visualstudio&logoColor=white)](https://marketplace.visualstudio.com/items?itemName=SureshSubramanian.DiffRapid)
[![Release](https://img.shields.io/github/v/release/isureshsubramanian/DiffRapid?include_prereleases&label=release)](https://github.com/isureshsubramanian/DiffRapid/releases)
[![CI](https://github.com/isureshsubramanian/DiffRapid/actions/workflows/ci.yml/badge.svg)](https://github.com/isureshsubramanian/DiffRapid/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-10%20%7C%20Standard%202.0-512BD4)
![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2026-5C2D91)
![Tests](https://img.shields.io/badge/tests-946%20passing-brightgreen)
![Status](https://img.shields.io/badge/status-preview-orange)

**[Install from the Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=SureshSubramanian.DiffRapid)**

[Features](#features) ·
[Getting started](#getting-started) ·
[Command line](#command-line) ·
[AI and MCP](#ai-and-mcp) ·
[Quality](#quality-and-testing) ·
[Performance](#performance) ·
[Architecture](#architecture)

</div>

---

DiffRapid compares files, folders, documents, images, tables, binaries and .NET APIs without leaving the IDE.
It shows differences in the real Visual Studio editor, so syntax highlighting, IntelliSense and Go To Definition
keep working while you compare. The same engine powers the `diffrapid` command line and an MCP server that AI
agents can call.

> [!NOTE]
> **Preview.** DiffRapid is under active development. Install it from the
> [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=SureshSubramanian.DiffRapid)
> and please report problems and ideas in [Issues](https://github.com/isureshsubramanian/DiffRapid/issues).

## Highlights

- **Editor-native text compare.** Side-by-side panes built on the Visual Studio editor, compared live as you type.
- **Three-way merge** with automatic resolution of safe conflicts, and conflict markers compatible with Git.
- **Folders at scale.** 200,000 files compared by size and date in about a second.
- **Beyond text.** Hex, image, CSV/TSV/Excel tables, Word/Excel/PowerPoint text, JSON/XML/YAML structure,
  C# members and the public API of assemblies and NuGet packages.
- **Local history.** Every save is kept, so you can compare with any earlier version, even outside source control.
- **AI that asks first.** Opt-in explanations and conflict proposals from a service you choose (OpenAI, Azure OpenAI,
  Ollama, LM Studio); secrets are masked and nothing changes until you accept it.
- **Agent-ready.** A read-only MCP server gives Claude Code, Copilot and other agents structured diffs.

## Features

### Text compare

| | |
|---|---|
| **Views** | Side-by-side panes on the Visual Studio editor: syntax colours, line numbers, aligned gaps, synchronized scrolling, overview bar |
| **Algorithms** | Histogram (default), Myers, Minimal, Patience, None |
| **Ignore options** | Whitespace (changes or all), case, blank lines, line endings, numbers, a missing final line ending, regular-expression line filters and substitutions |
| **Detail** | Word- and character-level changes inside lines, moved-block detection |
| **Unimportant differences** | *Comments & formatting* greys out changes that do not affect the code (Roslyn for C#, comment syntax for other languages) |
| **Live** | Compares unsaved editor contents and re-compares 300 ms after each edit |
| **Actions** | Copy to left/right, save either side, create a patch, export an HTML or Markdown report |
| **Plugins** | Unpackers (zip archives, external programs) and prediffers (`PrettifyJson sort`, `PrettifyXml`, `JsonQuery`, `SortLines`, `IgnoreColumns`, ...) chained into a read-only comparison |
| **Entry points** | Compare Selected, Compare with Selected, Compare with Clipboard, Compare with Saved File, Compare with Earlier Version (Git and TFVC), Compare with Local History |

### Merge

- Three-way merge with Left, Base and Right panes and an editable Result.
- Take Left, Base, Right, Left + right or Right + left per conflict, or for all conflicts at once.
- **Auto-resolve** settles conflicts that are identical, whitespace-only or touch different lines.
- **Propose** asks the configured AI service for a resolution; it is applied only through *Use proposal*.
- Git-style and diff3-style conflict markers.

### Folder compare

- Tree or flat view, with summary cards for changed, one-sided, renamed or moved, and identical files.
- Compare by full contents, quick contents, binary, xxHash64, date, date and size, size or existence.
- Rename and move detection, include and exclude masks, WinMerge-style `.flt` filter files, time tolerance.
- Three-way folder compare, with conflicts opening a merge.
- **Synchronize** with mirror and update profiles, a preview of every operation, and deletes to the Recycle Bin.
- HTML and CSV reports, optionally with an embedded diff of every changed file.

### Specialized views

| View | What it does |
|---|---|
| **Hex** | Binary files byte by byte, with alignment of inserted and deleted bytes, 8/16/32 bytes per line |
| **Image** | Side by side, highlight, XOR, blend, swipe and subtract, with tolerance and offset |
| **Table** | CSV, TSV and Excel sheets matched by key columns (suggested automatically), numeric tolerance, ignored columns |
| **Office** | `.docx`, `.xlsx` and `.pptx` compared as text, in read-only panes |
| **Structure** | JSON, XML, YAML, MSBuild and `.resx` compared by keys; list items matched by `id`, `name` or `key` |
| **C# semantics** | Member-by-member comparison through Roslyn, ignoring usings, formatting, comments or regions |
| **.NET API** | Public API of `.dll`, `.exe`, `.winmd` and `.nupkg` files, with breaking changes marked |

### Patches and history

- **Apply Patch** previews every file, applies only the files you select, supports reverse and fuzz, and writes atomically.
- **Local history** keeps a copy of each saved file (configurable retention). Use *Compare with Local History*
  from the editor or Solution Explorer, or `diffrapid history` on the command line.

### Keyboard

| Shortcut | Command |
|---|---|
| `F8` / `Shift+F8` | Next / previous difference or conflict |
| `Ctrl+F8` / `Ctrl+Shift+F8` | Copy to right / copy to left (merge: take right / take left) |
| `Alt+Down` / `Alt+Up` | Next / previous difference |
| `Alt+Right` / `Alt+Left` | Copy to right / copy to left |

Colours are customizable in **Tools > Options > Environment > Fonts and Colors** (items starting with "DiffRapid").
Behaviour is configured in **Tools > Options > DiffRapid** (File Comparison, Folder Comparison, Local History, AI).

## Getting started

### Install

Requires Visual Studio 2026 on Windows (x64 or ARM64). Visual Studio 2022 17.14 can install it too, but is not yet tested.

- **From Visual Studio:** open **Extensions > Manage Extensions**, search for **DiffRapid**, install it and restart
  Visual Studio.
- **From the Marketplace:** use the **Install** button on the
  [DiffRapid listing](https://marketplace.visualstudio.com/items?itemName=SureshSubramanian.DiffRapid).
- **Offline:** download `DiffRapid-<version>.vsix` from [Releases](https://github.com/isureshsubramanian/DiffRapid/releases)
  and double-click it.

### First comparison

1. Select two files in Solution Explorer, then right-click and choose **Compare Selected with DiffRapid**.
2. Or use **Tools > DiffRapid: Compare Files...**, **Compare Folders...** or **Merge Files...**.

### Build from source

Requirements:

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2026 with the **Visual Studio extension development** workload (for the extension)

```powershell
git clone https://github.com/isureshsubramanian/DiffRapid.git
cd DiffRapid

# Engine, command line and tests
dotnet build src/DiffRapid.Cli
dotnet test --project tests/DiffRapid.Core.Tests

# Visual Studio extension (needs Visual Studio's MSBuild)
msbuild src/DiffRapid.VisualStudio/DiffRapid.VisualStudio.csproj -restore
```

The extension package is written to `src/DiffRapid.VisualStudio/bin/<Configuration>/net472/DiffRapid.VisualStudio.vsix`.
Double-click it to install, or open `DiffRapid.slnx`, set `DiffRapid.VisualStudio` as the startup project and press
**F5** to run it in the experimental instance.

## Command line

`diffrapid` exposes the engine for scripts, CI and Git. Exit codes: `0` identical or merged cleanly,
`1` different or conflicts remain, `2` error.

```powershell
diffrapid diff left.cs right.cs --ignore-whitespace change     # unified diff
diffrapid diff left.cs right.cs --report html -o diff.html     # HTML, Markdown or JSON report
diffrapid merge base.txt mine.txt theirs.txt -o result.txt     # three-way merge
diffrapid dir src-old src-new --method hash --renames --json   # folder compare
diffrapid sync build-a build-b --profile mirror-right --dry-run
diffrapid table old.csv new.csv --key Id --tolerance 0.01
diffrapid struct appsettings.old.json appsettings.json
diffrapid semantic Old.cs New.cs --ignore usings,format
diffrapid api MyLib.1.0.0.nupkg MyLib.2.0.0.nupkg --breaking-only
diffrapid history list Program.cs
```

| Command | Purpose |
|---|---|
| `diff`, `patch`, `apply` | Text compare, unified diffs, applying patches (reverse, fuzz, dry run) |
| `merge`, `resolve`, `split-conflicts` | Three-way merge and conflict handling |
| `dir`, `dir3`, `sync` | Two- and three-way folder compare, synchronization |
| `hex`, `image`, `table`, `struct`, `semantic`, `api` | Specialized comparisons |
| `log`, `diff --rev` | Compare with Git or TFVC revisions |
| `history` | List, show, diff, snapshot and prune the local history |
| `explain` | Ask an AI service to explain a diff or propose conflict resolutions |
| `plugins`, `info`, `git-config`, `mcp` | Utilities and setup |

WinMerge-style switches such as `/ignorews:1`, `/ignorecase`, `/ignoreblanklines` and `/ignoreeol` are accepted.
Run `diffrapid --help` for every option.

### Use with Git

```sh
diffrapid git-config --global    # prints the git config commands; review and run them in Git Bash
git difftool --tool=diffrapid
git mergetool --tool=diffrapid
```

The mergetool writes the merged file and returns `0` for a clean merge or `1` when conflicts remain.

## AI and MCP

### AI explanations and proposals

AI features are **off until you configure them** in **Tools > Options > DiffRapid > AI**.

- Works with any OpenAI-compatible endpoint (OpenAI, Ollama, LM Studio, ...) and Azure OpenAI.
- The API key is read from an environment variable and never stored in settings; local servers need none.
- Before the first request in a session, DiffRapid shows how much text will be sent and where.
- Passwords, keys, tokens, private keys and connection strings are masked before sending (on by default).
- **Explain** streams a summary of a diff; **Propose** suggests a resolution for a merge conflict.
  Thinking models show their progress, and nothing is applied until you choose *Use proposal*.

### MCP server for agents

`diffrapid-mcp` is a read-only [Model Context Protocol](https://modelcontextprotocol.io) server over stdio with
eight tools: `compare_files`, `compare_text`, `compare_folders`, `merge_three_way`, `compare_structure`,
`compare_api`, `semantic_diff` and `create_patch`.

```powershell
dotnet build src/DiffRapid.Mcp
claude mcp add diffrapid -- dotnet <repo>\src\DiffRapid.Mcp\bin\Debug\net10.0\diffrapid-mcp.dll
```

See [src/DiffRapid.Mcp/README.md](src/DiffRapid.Mcp/README.md) for Claude Code, Visual Studio Copilot and other clients.

## Quality and testing

| | |
|---|---|
| **Test suite** | 946 tests (xUnit v3 on Microsoft.Testing.Platform) covering diff algorithms, merge, folders, sync, patches, tables, images, hex, structure, semantics, API surfaces, reports, history, AI and MCP |
| **Continuous integration** | GitHub Actions builds and tests the engine and command line on Windows and Ubuntu, and builds the extension on Windows |
| **Safety** | Atomic writes for patches and sync, unsafe paths and links rejected, deletes to the Recycle Bin, AI requests tested without a network |
| **Analyzers** | Nullable reference types and Visual Studio threading analyzers across the solution |

```powershell
dotnet test --project tests/DiffRapid.Core.Tests
```

## Performance

Performance budgets are enforced by a dedicated harness, [`perf/DiffRapid.Perf`](perf/README.md). Each scenario runs
in a fresh process on deterministic, seeded input, and the harness also verifies the result counts.

| Scenario | Input | Budget | Measured |
|---|---|---:|---:|
| **T1** text | Two 200,000-line files (11.8 MB each), 50 scattered changed blocks | < 2 s, < 400 MB allocated | **0.36–0.49 s**, 205 MB |
| **T2** text, worst case | Same size, every line different | < 10 s, linear memory | **0.50 s**, 233 MB |
| **F1** folders | Two trees of 200,000 files in 20,000 folders, 1% changed, by size and date | < 15 s, < 1 GB managed | **1.0–1.4 s**, 76 MB peak |
| **Full** folders | The same trees, by full contents | reported | 12 s warm cache; slower on a cold disk |

Release build, .NET 10, Windows on ARM64 with four logical processors. Results depend on hardware and disk cache.

```powershell
dotnet build perf/DiffRapid.Perf -c Release
dotnet run --project perf/DiffRapid.Perf -c Release --no-build -- --runs 3
```

## Architecture

```
DiffRapid.Core          .NET Standard 2.0  compare, merge, folders, sync, patches, tables, images, hex, structure, history
DiffRapid.Documents     .NET Standard 2.0  Word, Excel and PowerPoint text extraction (Open XML SDK)
DiffRapid.Semantic      .NET Standard 2.0  C# member-level comparison (Roslyn)
DiffRapid.Ai            .NET Standard 2.0  OpenAI-compatible and Azure OpenAI client, prompt builder, secret masking
DiffRapid.Cli           .NET 10            diffrapid command line
DiffRapid.Mcp           .NET 10            diffrapid-mcp server (ModelContextProtocol SDK)
DiffRapid.VisualStudio  .NET Framework 4.7.2  Visual Studio 2026 extension (VSIX)
```

The engine has no UI dependencies, so the extension, command line and MCP server share the same results.

| Path | Contents |
|---|---|
| `src/` | Engine, extension, command line, MCP server |
| `tests/DiffRapid.Core.Tests` | Test suite |
| `perf/DiffRapid.Perf` | Performance harness and budgets |
| `.github/workflows` | Continuous integration |

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first: DiffRapid is a clean-room
implementation, and contributions must not copy, port or translate source code from copyleft projects.

## License

DiffRapid is licensed under the [Apache License 2.0](LICENSE). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
