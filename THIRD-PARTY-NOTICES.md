# Third-party notices

## Runtime dependencies

| Component | License | Used for |
|---|---|---|
| System.Text.Json (and its .NET Standard support packages) | MIT | JSON output of comparisons and the JSON plugins (DiffRapid.Core) |
| System.Reflection.Metadata | MIT | Reading assembly metadata for the API compare (part of .NET; a package on .NET Standard 2.0) |
| DocumentFormat.OpenXml | MIT | Reading Word, Excel and PowerPoint files as text (DiffRapid.Documents) |
| Microsoft.CodeAnalysis.CSharp (Roslyn) | MIT | C# member matching and ignore-trivia compare (DiffRapid.Semantic) |

## Build and test dependencies (not redistributed)

| Component | License | Used for |
|---|---|---|
| Microsoft Visual Studio SDK | Microsoft Visual Studio SDK license | Compiling the extension (reference assemblies only) |
| Microsoft.VSSDK.BuildTools | Microsoft license | Building the VSIX package |
| Microsoft.VisualStudio.Threading.Analyzers | MIT | Static analysis |
| xunit.v3 | Apache-2.0 | Unit tests |
| DiffPlex | Apache-2.0 | Independent reference implementation used in tests |
