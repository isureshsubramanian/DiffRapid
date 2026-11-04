# Performance budgets

Build and run from the repository root:

```powershell
dotnet build perf/DiffRapid.Perf -c Release
dotnet run --project perf/DiffRapid.Perf -c Release --no-build -- --runs 3 --data perf/.data
```

Without `--data`, inputs are generated in a unique temporary directory and deleted afterwards. With `--data`, the harness retains its `diffrapid-perf-v1` subdirectory and reuses completed inputs. Generation is outside the measurements and can take several minutes for the 400,000 physical files. Do not edit retained inputs; use a new data directory to regenerate them.

Use `--scenario T1`, `--scenario T2`, `--scenario F1`, or `--scenario Full` to run one scenario. Text-only scenarios do not generate the folder trees. `--runs` defaults to 3 and must be positive. Each measurement runs in a fresh child process. The harness checks the resulting counts as well as performance, and exits 1 if any scenario fails. T2 has a 10-second cancellation deadline and a 15-second process watchdog; other scenarios have a 10-minute cancellation deadline and an 11-minute watchdog. Ctrl+C cancels generation or terminates the active worker.

| Scenario | Deterministic input and operation | Budget |
| --- | --- | --- |
| T1 | Two 200,000-line UTF-8 files, 11,800,000 bytes each; 4,000 changed lines in 50 scattered 80-line hunks. Read, parse, and compare with default options (Histogram, moved-block detection enabled). | Median <2,000 ms; total allocation <400,000,000 bytes |
| T2 | Same lengths, every line different; read, parse, and compare with default options. | Median <10,000 ms; bounded replacement fallback and linear memory |
| F1 | Two trees, each containing 200,000 64-byte files in 20,000 folders. Exactly 1% have different contents and timestamps; sizes match. Compare using DateAndSize and default parallelism. | Median <15,000 ms; managed memory <1,000,000,000 bytes |
| Full | The same trees, compared using FullContents and default parallelism. | Report time; no time budget |

Text and folder payloads use seeded `Random` instances (2531 and 2532). File timestamps are fixed, with changed files five seconds newer. The folder result must contain 220,000 items, 2,000 different files, and no errors. T1 must report exactly 4,000 replaced lines on each side; T2 must report 200,000.

Time uses `Stopwatch`; allocation uses the process-wide difference in `GC.GetTotalAllocatedBytes(true)`. The table reports median time and the maximum memory values across runs. Managed heap size is sampled every 10 ms with `GC.GetTotalMemory(false)` and at completion; this is an observed peak rather than a precise allocation profiler. Peak working set is the worker process's lifetime `PeakWorkingSet64`, including startup. Total allocated bytes conservatively bound peak allocation: T1 enforces the cumulative limit, and F1 requires both its sampled peak and cumulative allocation to stay below 1 GB. MB in the tables means decimal MB. Filesystem caches are not flushed between runs; process startup is excluded, while comparison JIT and result validation are included.

## Measurements

Baseline: `f8a457d`. Release, .NET SDK 10.0.403 / runtime 10.0.14, Windows 10.0.26100, ARM64, four logical processors, in the provided development environment. The same retained dataset is used before and after. These figures describe this host; rerun on the target laptop for its own budget check.

| Scenario | Before ms | After ms | Before allocated MB | After allocated MB | After budget |
| --- | ---: | ---: | ---: | ---: | --- |
| T1 | 847.3 | 490.7 | 315.04 | 204.84 | PASS |
| T2 | 10,010.9 (cancelled) | 501.9 | 337.83 (partial) | 233.03 | PASS |
| F1 | 17,208.8 | 1,352.9 | 319.75 | 339.59 | PASS |
| Full | 21,854.8 | 12,049.8 | 52,906.10 | 431.80 | Reported |

Every row uses three runs. Full-content figures are from repeated scans with warm filesystem caches, run sequentially without overlapping the test suite. Disk/cache state matters: an earlier scan of the final optimized engine took **166,709.3 ms**, allocated **429.95 MB**, and reached **75.89 MB** sampled managed memory / **118.35 MB** peak working set. Both scans validated all 220,000 result items and 2,000 changed files. The large allocation reduction is consistent; cold scan time depends heavily on storage and background IO.

| Scenario | Before managed peak MB | After managed peak MB | Before working set MB | After working set MB |
| --- | ---: | ---: | ---: | ---: |
| T1 | 115.88 | 148.75 | 173.43 | 180.07 |
| T2 | 169.27 | 168.23 | 213.82 | 200.61 |
| F1 | 72.11 | 75.00 | 119.84 | 120.15 |
| Full | 92.40 | 75.29 | 152.25 | 126.80 |

## Changes

* Line interning uses `(content, ending)` keys and reuses unmodified content strings. The default path previously rebuilt each line in a `StringBuilder` and then allocated another string with an ending suffix. Normalization options still run their existing transformations. Key generation now has a cancellation overload.
* Myers spends at most 1,000,000 forward/backward frontier visits per search, shared across its recursive subranges. If exhausted, it marks the unresolved ranges as replacements, preserving common prefixes, suffixes, and already-found matches. The result is valid and round-trips through a patch, though it can contain more edits. Histogram and Patience inherit this bound in their Myers fallback regions. The explicitly selected Minimal algorithm retains its exact, uncapped search and supports cancellation.
* Histogram falls back to bounded Myers for sparse integer keys rather than allocating arrays proportional to the largest key. Its memory is therefore proportional to input length even for external keys near `int.MaxValue`.
* Equally rare, equally long Histogram anchors prefer the most balanced split. Repeatedly selecting the first anchor previously exhausted the depth limit on many scattered single-line edits. Balanced splits preserve those individual hunks and keep the partitioning work near `O(n log n)` for that case.
* Move detection avoids indexing a single replacement hunk, since a move requires two different hunks, and observes cancellation during indexing and matching.
* Folder enumeration carries cached `FileSystemInfo` metadata into comparison, replacing repeated attribute, size, and timestamp queries. Metadata methods use that snapshot; content methods still check existence after reading. One-sided entries are checked for disappearance. This trades a small allocation increase for much less metadata IO.
* Folder content comparison reuses two 64 KB buffers per worker and disables redundant `FileStream` buffering. Previously each file pair allocated fresh buffers, even for tiny files. Buffer memory is bounded by active workers rather than file count. Once both fills reach EOF, comparison returns without issuing another pair of EOF reads.
* Inline comparison caps either input at 10,000 characters, using whole-line spans above the cap. HTML skips inline marks for hunks exceeding 200 lines on either side. Reports still contain every changed line. Inline and report work observes cancellation between searches and rows.
* Folder reports with embedded diffs stream the base page directly to the destination rather than constructing and copying an entire HTML string first. Public signatures are preserved, with cancellation overloads added where needed.

No packages were added. The harness and engine changes use the repository's Apache-2.0 licence.

## Validation

`PerfGuardTests.cs` covers bounded fallback and patch round-tripping in both directions, retained Minimal behavior, sparse Histogram keys, ending-sensitive interning, cancellation, oversized inline work, complete huge-hunk output, and buffer reuse across boundary sizes and parallel workers. Its inputs are small; the performance harness is a separate console project and is not part of the test run.

The complete test run passed: **733 total, 732 passed, 1 skipped**, including **15 new guard cases**. The existing symlink test was skipped because this environment lacks symbolic-link privileges. All managed projects built with **0 warnings and 0 errors**, in both Debug and Release. The default three-run setting and temporary text-input generation/cleanup were also exercised successfully.

In this sandbox, NuGet's audit endpoint was unavailable, so restore used `-p:NuGetAudit=false`. The unmodified VSIX project's package restore also failed; all managed projects were built with zero warnings through a generated solution filter excluding VSIX, using `--disable-build-servers -m:1` to avoid reusing build workers from other worktrees.
