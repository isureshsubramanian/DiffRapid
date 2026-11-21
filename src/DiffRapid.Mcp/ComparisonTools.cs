using System.Text;
using System.Text.Json;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Assemblies;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Output;
using DiffRapid.Core.Structured;
using DiffRapid.Core.Text;
using DiffRapid.Documents;
using DiffRapid.Semantic;

namespace DiffRapid.Mcp;

internal static class ComparisonTools
{
    private const int MaxEntries = 1000;
    private const long MaxFileBytes = 64L * 1024 * 1024;

    internal static object Run(string name, JsonElement arguments, CancellationToken cancellationToken) => name switch
    {
        "compare_files" => CompareText(arguments, true, cancellationToken),
        "compare_text" => CompareText(arguments, false, cancellationToken),
        "compare_folders" => CompareFolders(arguments, cancellationToken),
        "merge_three_way" => Merge(arguments, cancellationToken),
        "compare_structure" => CompareStructure(arguments, cancellationToken),
        "compare_api" => CompareApi(arguments, cancellationToken),
        "semantic_diff" => SemanticDiff(arguments, cancellationToken),
        "create_patch" => CreatePatch(arguments, cancellationToken),
        _ => throw new ArgumentException($"Unknown tool '{name}'."),
    };

    private static object CompareText(JsonElement arguments, bool files, CancellationToken cancellationToken)
    {
        var leftLabel = files ? Required(arguments, "left") : "left";
        var rightLabel = files ? Required(arguments, "right") : "right";
        var leftText = files ? ReadText(leftLabel, true, cancellationToken) : Required(arguments, "left_text");
        var rightText = files ? ReadText(rightLabel, true, cancellationToken) : Required(arguments, "right_text");
        var options = TextOptions(arguments);
        var context = Integer(arguments, "context", 3, 1000);
        var maxHunks = Integer(arguments, "max_hunks", 100, MaxEntries);
        var result = TextComparer.Compare(ParseText(leftText), ParseText(rightText), options, cancellationToken);
        var budget = new ReportBudget(cancellationToken);
        var hunks = new List<object>();
        var selected = new List<DiffHunk>();
        foreach (var hunk in result.Hunks.Take(maxHunks))
        {
            if (budget.Exhausted)
            {
                break;
            }

            var leftLines = Lines(result.Left, hunk.LeftStart, hunk.LeftCount, budget);
            var rightLines = Lines(result.Right, hunk.RightStart, hunk.RightCount, budget);
            hunks.Add(new
            {
                kind = Camel(hunk.Kind),
                ignored = hunk.Significance == DiffSignificance.Ignored,
                leftLine = hunk.LeftStart + 1,
                leftCount = hunk.LeftCount,
                rightLine = hunk.RightStart + 1,
                rightCount = hunk.RightCount,
                left = leftLines,
                right = rightLines,
            });
            selected.Add(hunk);
        }

        var limited = new TextDiffResult(result.Left, result.Right, options, selected, []);
        var unified = Render(writer => UnifiedDiffWriter.Write(writer, limited, leftLabel, rightLabel, context), cancellationToken);
        var stats = CountLines(result, cancellationToken);
        return new
        {
            version = JsonReportWriter.SchemaVersion,
            kind = "file",
            left = new { label = leftLabel, lineCount = result.Left.LineCount },
            right = new { label = rightLabel, lineCount = result.Right.LineCount },
            identical = result.AreEqual,
            differenceCount = result.DifferenceCount,
            lines = new { changed = stats.Changed, added = stats.Added, removed = stats.Removed },
            hunks,
            moves = result.MovedBlocks.Take(MaxEntries).Select(move => new { leftLine = move.LeftStart + 1, rightLine = move.RightStart + 1, lineCount = move.LineCount }).ToArray(),
            unifiedText = unified.Text,
            truncated = budget.Truncated || unified.Truncated || hunks.Count < result.Hunks.Count || result.MovedBlocks.Count > MaxEntries,
        };
    }

    private static object CompareFolders(JsonElement arguments, CancellationToken cancellationToken)
    {
        var left = DirectoryPath(arguments, "left");
        var right = DirectoryPath(arguments, "right");
        var method = Optional(arguments, "method", "full") switch
        {
            "full" => FolderCompareMethod.FullContents,
            "quick" => FolderCompareMethod.QuickContents,
            "binary" => FolderCompareMethod.BinaryContents,
            "date" => FolderCompareMethod.Date,
            "date-size" => FolderCompareMethod.DateAndSize,
            "size" => FolderCompareMethod.Size,
            "existence" => FolderCompareMethod.Existence,
            "hash" => FolderCompareMethod.Hash,
            var value => throw new ArgumentException($"Unknown folder method '{value}'."),
        };
        var maxEntries = Integer(arguments, "max_entries", 100, MaxEntries);
        var filter = Optional(arguments, "filter", "");
        var options = new FolderCompareOptions
        {
            Method = method,
            Recursive = Boolean(arguments, "recursive", true),
            IncludeFiles = filter.Length == 0 ? [] : [filter],
        };
        var result = FolderComparer.Compare(left, right, options, cancellationToken: cancellationToken);
        EnsureFolderReadable(result);
        var visible = result.Items.Where(item => item.Status != FolderCompareStatus.Skipped
            && (Boolean(arguments, "include_identical") || item.Status != FolderCompareStatus.Identical)).ToList();
        var selected = visible.Take(maxEntries).ToArray();
        var budget = new ReportBudget(cancellationToken);
        var items = new List<object>();
        foreach (var item in selected)
        {
            if (budget.Exhausted)
            {
                break;
            }

            items.Add(new
            {
                path = budget.Text(Slashes(item.RelativePath)),
                type = Camel(item.Kind),
                status = Camel(item.Status),
                differenceCount = item.DifferenceCount,
                error = item.Error is null ? null : budget.Text(item.Error),
                left = Metadata(item.Left),
                right = Metadata(item.Right),
            });
        }

        return new
        {
            version = JsonReportWriter.SchemaVersion,
            kind = "folder",
            leftRoot = Slashes(result.LeftRoot),
            rightRoot = Slashes(result.RightRoot),
            status = Camel(result.Root.Status),
            error = result.Root.Error,
            counts = Enum.GetValues<FolderCompareStatus>().ToDictionary(Camel, status => result.Counts.TryGetValue(status, out var count) ? count : 0),
            items,
            truncated = budget.Truncated || items.Count < visible.Count,
        };
    }

    private static (int Changed, int Added, int Removed) CountLines(TextDiffResult result, CancellationToken cancellationToken)
    {
        var changed = 0;
        var added = 0;
        var removed = 0;
        foreach (var hunk in result.ImportantHunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paired = Math.Min(hunk.LeftCount, hunk.RightCount);
            changed += paired;
            added += hunk.RightCount - paired;
            removed += hunk.LeftCount - paired;
        }

        return (changed, added, removed);
    }

    private static object Merge(JsonElement arguments, CancellationToken cancellationToken)
    {
        var baseText = ReadText(Required(arguments, "base"), false, cancellationToken);
        var leftText = ReadText(Required(arguments, "left"), false, cancellationToken);
        var rightText = ReadText(Required(arguments, "right"), false, cancellationToken);
        var result = ThreeWayMerger.Merge(ParseText(baseText), ParseText(leftText), ParseText(rightText), cancellationToken: cancellationToken);
        var budget = new ReportBudget(cancellationToken);
        var mergedText = budget.Text(result.ToText(includeBase: true));
        var conflicts = result.Chunks.Where(chunk => chunk.Kind == MergeChunkKind.Conflict).Take(MaxEntries).Select(chunk => new
        {
            baseLine = chunk.BaseStart + 1,
            baseCount = chunk.BaseEnd - chunk.BaseStart,
            leftLine = chunk.LeftStart + 1,
            leftCount = chunk.LeftEnd - chunk.LeftStart,
            rightLine = chunk.RightStart + 1,
            rightCount = chunk.RightEnd - chunk.RightStart,
        }).ToArray();
        return new { mergedText, conflictCount = result.ConflictCount, conflicts, truncated = budget.Truncated || conflicts.Length < result.ConflictCount };
    }

    private static object CompareStructure(JsonElement arguments, CancellationToken cancellationToken)
    {
        var left = Required(arguments, "left");
        var result = StructuredComparer.Compare(ReadText(left, false, cancellationToken),
            ReadText(Required(arguments, "right"), false, cancellationToken), fileName: left, cancellationToken: cancellationToken);
        var budget = new ReportBudget(cancellationToken);
        var changes = new List<object>();
        foreach (var change in result.Differences.Take(MaxEntries))
        {
            if (budget.Exhausted)
            {
                break;
            }

            changes.Add(new
            {
                kind = Camel(change.Kind),
                path = budget.Text(change.Path),
                left = change.Left?.Value is { } leftValue ? budget.Text(leftValue) : null,
                right = change.Right?.Value is { } rightValue ? budget.Text(rightValue) : null,
                leftSpan = change.LeftSpan,
                rightSpan = change.RightSpan,
            });
        }

        return new { identical = result.IsIdentical, format = Camel(result.Format), differenceCount = result.Differences.Count, changes, truncated = budget.Truncated || changes.Count < result.Differences.Count };
    }

    private static object CompareApi(JsonElement arguments, CancellationToken cancellationToken)
    {
        var left = AssemblyPath(Required(arguments, "left"));
        var right = AssemblyPath(Required(arguments, "right"));
        var leftExtension = Path.GetExtension(left).ToLowerInvariant();
        var rightExtension = Path.GetExtension(right).ToLowerInvariant();
        var budget = new ReportBudget(cancellationToken);
        if (leftExtension == ".nupkg" && rightExtension == ".nupkg")
        {
            var result = AssemblyComparer.Compare(NuGetPackageReader.Read(left, cancellationToken), NuGetPackageReader.Read(right, cancellationToken));
            var libraries = new List<object>();
            foreach (var library in result.Libraries.Take(MaxEntries))
            {
                if (budget.Exhausted)
                {
                    break;
                }

                libraries.Add(new { path = budget.Text(library.Path), comparison = ApiReport(library.Comparison, budget) });
            }

            var infoChanges = InfoChanges(result.InfoChanges, budget);
            return new { identical = result.IsIdentical, hasBreakingChanges = result.HasBreakingChanges, infoChanges, libraries, truncated = budget.Truncated || libraries.Count < result.Libraries.Count };
        }

        if (leftExtension is not (".dll" or ".exe") || rightExtension is not (".dll" or ".exe"))
        {
            throw new ArgumentException("Use two .dll/.exe assemblies or two .nupkg packages.");
        }

        return ApiReport(AssemblyComparer.Compare(ApiReader.Read(left, cancellationToken), ApiReader.Read(right, cancellationToken)), budget);
    }

    // The readers open the file themselves; a length of 0 is an empty file or a special file that must not be opened.
    private static string AssemblyPath(string path) =>
        FilePath(path) == 0 ? throw new ArgumentException($"'{path}' is empty or not a regular file.") : path;

    private static object ApiReport(ApiComparison result, ReportBudget budget)
    {
        var infoChanges = InfoChanges(result.InfoChanges, budget);
        var changes = new List<object>();
        foreach (var change in result.Changes.Take(MaxEntries))
        {
            if (budget.Exhausted)
            {
                break;
            }

            changes.Add(new
            {
                kind = Camel(change.Kind),
                isBreaking = change.IsBreaking,
                @namespace = budget.Text(change.Entry.Namespace),
                declaration = budget.Text(change.Entry.Declaration),
                typeName = budget.Text(change.Entry.TypeName),
                newDeclaration = change.NewEntry is null ? null : budget.Text(change.NewEntry.Declaration),
            });
        }

        if (changes.Count < result.Changes.Count)
        {
            budget.MarkTruncated();
        }

        return new { identical = result.IsIdentical, hasBreakingChanges = result.HasBreakingChanges, differenceCount = result.Changes.Count + result.InfoChanges.Count, infoChanges, changes, truncated = budget.Truncated };
    }

    private static object[] InfoChanges(IReadOnlyList<ApiInfoChange> changes, ReportBudget budget)
    {
        var entries = new List<object>();
        foreach (var change in changes.Take(MaxEntries))
        {
            if (budget.Exhausted)
            {
                break;
            }

            entries.Add(new { name = budget.Text(change.Name), left = change.Left is null ? null : budget.Text(change.Left), right = change.Right is null ? null : budget.Text(change.Right), isBreaking = change.IsBreaking });
        }

        if (entries.Count < changes.Count)
        {
            budget.MarkTruncated();
        }

        return entries.ToArray();
    }

    private static object SemanticDiff(JsonElement arguments, CancellationToken cancellationToken)
    {
        var left = ReadText(Required(arguments, "left"), false, cancellationToken);
        var right = ReadText(Required(arguments, "right"), false, cancellationToken);
        var result = MemberMatcher.Compare(left, right, cancellationToken: cancellationToken);
        var changed = result.Flatten().Where(member => member.Status != MemberDiffStatus.Unchanged).ToList();
        var budget = new ReportBudget(cancellationToken);
        var members = new List<object>();
        foreach (var member in changed.Take(MaxEntries))
        {
            if (budget.Exhausted)
            {
                break;
            }

            members.Add(new
            {
                status = Camel(member.Status),
                kind = Camel(member.Kind),
                signature = budget.Text(member.Signature),
                qualifiedName = budget.Text((member.Right ?? member.Left)!.QualifiedName),
                left = MemberRange(member.Left),
                right = MemberRange(member.Right),
                textChanged = member.TextChanged,
            });
        }

        return new { identical = result.AreEqual, leftHasErrors = result.LeftHasErrors, rightHasErrors = result.RightHasErrors, differenceCount = changed.Count, members, truncated = budget.Truncated || members.Count < changed.Count };
    }

    private static object CreatePatch(JsonElement arguments, CancellationToken cancellationToken)
    {
        var left = LocalPath(Required(arguments, "left"));
        var right = LocalPath(Required(arguments, "right"));
        RenderedText patch;
        if (Directory.Exists(left) || Directory.Exists(right))
        {
            left = DirectoryPath(arguments, "left");
            right = DirectoryPath(arguments, "right");

            // A byte comparison stops at the first differing byte; only changed files are then read, once, by the
            // patch writer, which stops when the output limit is reached. Files whose bytes differ but whose text
            // matches get a bare "diff --git" line, which Patch.Parse skips.
            var options = new FolderCompareOptions { Method = FolderCompareMethod.BinaryContents };
            var result = FolderComparer.Compare(left, right, options, cancellationToken: cancellationToken);
            EnsureFolderReadable(result);
            EnsurePatchSizes(result);
            patch = Render(writer => FolderPatchWriter.Write(writer, result, CompareOptions.Default), cancellationToken);
        }
        else
        {
            var result = TextComparer.Compare(ParseText(ReadText(left, false, cancellationToken)), ParseText(ReadText(right, false, cancellationToken)), cancellationToken: cancellationToken);

            // Both labels name the left file on purpose: apply then patches that file in place. Different names
            // would make the patch rename the left file to the right file's name.
            patch = Render(writer => UnifiedDiffWriter.Write(writer, result, "a/" + Path.GetFileName(left), "b/" + Path.GetFileName(left)), cancellationToken);
        }

        return new { patch = patch.Text, truncated = patch.Truncated };
    }

    // The patch writer reads each changed file whole, so the per-file limit applies to folder entries too.
    private static void EnsurePatchSizes(FolderCompareResult result)
    {
        foreach (var item in result.Items)
        {
            var changed = item.Status is FolderCompareStatus.Different or FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly;
            if (item.Kind == FolderItemKind.File && changed
                && Math.Max(item.Left.Exists ? item.Left.Size : 0, item.Right.Exists ? item.Right.Size : 0) > MaxFileBytes)
            {
                throw new ArgumentException($"'{Slashes(item.RelativePath)}' exceeds the 64 MiB per-file limit.");
            }
        }
    }

    private static CompareOptions TextOptions(JsonElement arguments)
    {
        var algorithm = Optional(arguments, "algorithm", "histogram") switch
        {
            "default" => DiffAlgorithmKind.Default,
            "minimal" => DiffAlgorithmKind.Minimal,
            "patience" => DiffAlgorithmKind.Patience,
            "histogram" => DiffAlgorithmKind.Histogram,
            "none" => DiffAlgorithmKind.None,
            var value => throw new ArgumentException($"Unknown algorithm '{value}'."),
        };
        return new CompareOptions
        {
            Algorithm = algorithm,
            Whitespace = Boolean(arguments, "ignore_whitespace") ? WhitespaceMode.IgnoreAll : WhitespaceMode.Compare,
            IgnoreCase = Boolean(arguments, "ignore_case"),
            IgnoreBlankLines = Boolean(arguments, "ignore_blank_lines"),
        };
    }

    private static string ReadText(string path, bool extractDocuments, CancellationToken cancellationToken)
    {
        var length = FilePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (extractDocuments && length > 0)
        {
            var documentText = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".docx" => DocxText.Extract(path, cancellationToken: cancellationToken),
                ".xlsx" => XlsxText.Extract(path, cancellationToken: cancellationToken),
                ".pptx" => PptxText.Extract(path, cancellationToken: cancellationToken),
                _ => null,
            };
            if (documentText is not null)
            {
                return documentText;
            }
        }

        var decoded = ReadDecoded(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.IsBinary)
        {
            throw new ArgumentException($"'{path}' is binary; use compare_api for .NET assemblies.");
        }

        return decoded.Text;
    }

    private static DecodedText ReadDecoded(string path, CancellationToken cancellationToken)
    {
        var length = FilePath(path);

        // Devices, pipes and sockets report a length of 0. They are never opened, so a read cannot block, run
        // without end, or consume another stream (such as this server's own standard input).
        if (length == 0)
        {
            return TextFileReader.Decode([]);
        }

        // Read at most one byte past the limit, so a file that grew after the length check is still refused.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var content = new MemoryStream((int)Math.Min(length, MaxFileBytes));
        var buffer = new byte[81920]; // the default Stream.CopyTo buffer size
        int read;
        while ((read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxFileBytes + 1 - content.Length))) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            content.Write(buffer, 0, read);
            if (content.Length > MaxFileBytes)
            {
                throw new ArgumentException($"'{path}' exceeds the 64 MiB per-file limit.");
            }
        }

        return TextFileReader.Decode(content.ToArray());
    }

    private static TextDocument ParseText(string text)
    {
        if (text.Length > MaxFileBytes)
        {
            throw new ArgumentException("Text exceeds the 64 MiB character limit.");
        }

        return TextDocument.Parse(text);
    }

    /// <summary>
    /// Checks that <paramref name="path"/> names an existing file within the size limit and returns its length.
    /// A length of 0 also covers devices, pipes and sockets, which callers must not open.
    /// </summary>
    private static long FilePath(string path)
    {
        LocalPath(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"File does not exist: '{path}'.");
        }

        // Device nodes are also refused by name, for a clear error. File attributes do not mark devices on
        // macOS or Linux, so the zero-length rule is what keeps other special files from being opened.
        var target = file.LinkTarget is null ? file.FullName : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
        if (!OperatingSystem.IsWindows() && (file.FullName.StartsWith("/dev/", StringComparison.Ordinal) || target.StartsWith("/dev/", StringComparison.Ordinal)))
        {
            throw new ArgumentException($"'{path}' is a device, not a regular file.");
        }

        if (file.Length > MaxFileBytes)
        {
            throw new ArgumentException($"'{path}' exceeds the 64 MiB per-file limit.");
        }

        return file.Length;
    }

    /// <summary>
    /// Refuses network and device paths before any file-system call: the \\server\share, //server/share, \\?\ and
    /// \\.\ prefixes, and on Windows device names such as NUL, which resolve to \\.\NUL.
    /// </summary>
    private static string LocalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || (path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/'))
        {
            throw new ArgumentException($"'{path}' is not a local path; network and device paths are not supported.");
        }

        if (OperatingSystem.IsWindows() && Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{path}' is a device or network path, not a local file.");
        }

        return path;
    }

    private static string DirectoryPath(JsonElement arguments, string name)
    {
        var path = LocalPath(Required(arguments, name));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: '{path}'.");
        }

        return path;
    }

    private static void EnsureFolderReadable(FolderCompareResult result)
    {
        if (result.Root.Status == FolderCompareStatus.Error)
        {
            throw new IOException(result.Root.Error ?? "Cannot read the comparison directories.");
        }
    }

    private static object? Metadata(FileMeta metadata) => metadata.Exists
        ? new { size = metadata.Size, modifiedUtc = DateTime.SpecifyKind(metadata.LastWriteUtc, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture) }
        : null;

    private static object? MemberRange(CSharpMember? member) => member is null ? null
        : new { line = member.Span.StartLine + 1, count = member.Span.EndLine - member.Span.StartLine + 1 };

    private static string[] Lines(TextDocument document, int start, int count, ReportBudget budget)
    {
        var lines = new List<string>();
        for (var index = start; index < start + count; index++)
        {
            if (budget.Exhausted || lines.Count == MaxEntries)
            {
                budget.MarkTruncated();
                break;
            }

            lines.Add(budget.Text(document[index].Content));
        }

        return lines.ToArray();
    }

    private static string Required(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;

    private static string Optional(JsonElement arguments, string name, string fallback) => arguments.TryGetProperty(name, out var value) ? value.GetString()! : fallback;

    private static bool Boolean(JsonElement arguments, string name, bool fallback = false) => arguments.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;

    private static int Integer(JsonElement arguments, string name, int fallback, int maximum)
    {
        var value = arguments.TryGetProperty(name, out var element) ? element.GetInt32() : fallback;
        if (value < 0 || value > maximum)
        {
            throw new ArgumentException($"'{name}' must be between 0 and {maximum}.");
        }

        return value;
    }

    private static string Camel<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static RenderedText Render(Action<TextWriter> write, CancellationToken cancellationToken)
    {
        using var writer = new LimitedTextWriter(cancellationToken);
        try
        {
            write(writer);
            return new RenderedText(writer.ToString(), false);
        }
        catch (OutputLimitException)
        {
            return new RenderedText(writer.ToString(), true);
        }
    }

    private sealed record RenderedText(string Text, bool Truncated);

    private sealed class ReportBudget(CancellationToken cancellationToken)
    {
        private int _remaining = 64 * 1024;

        public bool Truncated { get; private set; }

        public bool Exhausted => _remaining == 0;

        public void MarkTruncated() => Truncated = true;

        public string Text(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(text.Length, _remaining);
            _remaining -= length;
            Truncated |= length < text.Length;
            return text[..length];
        }
    }

    private sealed class OutputLimitException : Exception;

    private sealed class LimitedTextWriter(CancellationToken cancellationToken) : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_text.Length >= 64 * 1024)
            {
                throw new OutputLimitException();
            }

            _text.Append(value);
        }

        public override void Write(string? value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is null)
            {
                return;
            }

            var length = Math.Min(value.Length, 64 * 1024 - _text.Length);
            _text.Append(value, 0, length);
            if (length < value.Length)
            {
                throw new OutputLimitException();
            }
        }

        public override string ToString() => _text.ToString();
    }
}
