# Contributing to DiffRapid

Thanks for your interest in DiffRapid.

## Clean-room rule

DiffRapid is licensed under the Apache License 2.0. To keep it that way:

- **Do not copy, port or translate source code** from GPL, LGPL-only, AGPL or other copyleft
  projects, including other diff and merge tools. This also applies to their translation strings,
  icons and color schemes.
- Other tools may be used as a **behavioral reference only**: their public documentation,
  documented file formats and observed behavior.
- Algorithms must be implemented from published descriptions (papers, specifications) or from
  permissively licensed sources with attribution.
- Every new third-party dependency must have a license compatible with Apache-2.0 and be listed in
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Code guidelines

- `DiffRapid.Core` targets .NET Standard 2.0 and has no UI or Visual Studio dependencies.
- The Visual Studio extension targets .NET Framework 4.7.2 and is compiled against the 17.14 SDK;
  features that only exist in newer Visual Studio versions must be detected at run time.
- Long-running work must be cancellable and must not run on the UI thread.
- Add tests for engine changes (`dotnet test --project tests/DiffRapid.Core.Tests`).
