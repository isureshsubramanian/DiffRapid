# DiffRapid

Compare and merge files and folders without leaving Visual Studio.

> **Status: early development.** The compare engine and command-line tool work; the Visual Studio
> extension is a first preview of side-by-side text compare.

## What works today

- **Compare engine** (`DiffRapid.Core`, .NET Standard 2.0)
  - Diff algorithms: Myers (default/minimal), Patience, Histogram, None
  - Ignore options: whitespace (change / all), case, blank lines, line endings, numbers,
    missing trailing line ending
  - Moved-block detection, word- and character-level inline differences
  - Three-way merge with automatic resolution of non-overlapping changes and git-style
    conflict markers (optionally diff3 style)
  - Unified diff (patch) output
  - Encoding detection: byte order marks, strict UTF-8, single-byte fallback
- **Command line** (`diffrapid`)
  - `diffrapid diff <left> <right>` prints a unified diff
  - `diffrapid merge <base> <left> <right> [-o out] [--diff3]`
  - Exit codes: `0` identical / merged cleanly, `1` different / conflicts, `2` error
- **Visual Studio 2026 extension** (preview)
  - *Compare Selected with DiffRapid* on two files in Solution Explorer, or
    *Tools > DiffRapid: Compare Files...*
  - Side-by-side view built on the Visual Studio editor: syntax highlighting, line numbers,
    aligned panes, synchronized scrolling, inline change highlighting, moved lines
  - Compares the live editor contents (including unsaved changes) and re-compares as you type
  - Overview bar, previous/next difference (Alt+Up / Alt+Down)
  - Colors are customizable in *Tools > Options > Environment > Fonts and Colors*
    (items starting with "DiffRapid")

## Building

Requirements: Windows, .NET 10 SDK, Visual Studio 2026 with the
*Visual Studio extension development* workload.

```powershell
# Engine, CLI and tests
dotnet build src/DiffRapid.Cli
dotnet test --project tests/DiffRapid.Core.Tests

# Visual Studio extension (needs Visual Studio's MSBuild)
msbuild src/DiffRapid.VisualStudio/DiffRapid.VisualStudio.csproj -restore
```

To debug the extension, open `DiffRapid.sln` in Visual Studio, set `DiffRapid.VisualStudio` as the
startup project and press F5; it launches the experimental instance (`/rootsuffix Exp`).

## Repository layout

| Path | Contents |
|---|---|
| `src/DiffRapid.Core` | UI-independent compare and merge engine |
| `src/DiffRapid.Cli` | `diffrapid` command-line tool |
| `src/DiffRapid.VisualStudio` | Visual Studio extension (VSIX) |
| `tests/DiffRapid.Core.Tests` | Engine tests |

## License

[Apache License 2.0](LICENSE). See [CONTRIBUTING.md](CONTRIBUTING.md) before contributing.
