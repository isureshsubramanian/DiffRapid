using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Folders;

/// <summary>
/// Compares two directory trees. Each item is reported through the progress callback as soon as its status
/// is known, so a UI can show results while the scan is still running.
/// </summary>
public static class FolderComparer
{
    private const int MaxDepth = 128;
    private const int BufferSize = 64 * 1024;
    private const int TextProbeSize = 8000;

    private static readonly FileMeta Missing = new(0, DateTime.MinValue, 0, false);

    private static readonly string[] NoiseFolders = { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults" };

    /// <summary>
    /// Scans synchronously. Cancellation throws <see cref="OperationCanceledException"/>; IO failures become
    /// <see cref="FolderCompareStatus.Error"/> items. With parallelism enabled, callbacks may come from worker threads
    /// (one at a time). With rename detection, paired files are reported a second time with their new status.
    /// </summary>
    public static FolderCompareResult Compare(
        string left,
        string right,
        FolderCompareOptions? options = null,
        IProgress<FolderCompareItem>? itemReported = null,
        CancellationToken cancellationToken = default)
    {
        Action<FolderCompareItem>? report = itemReported is null ? null : itemReported.Report;
        return Run(left, right, options, report, report, cancellationToken);
    }

    /// <summary>
    /// Scans like <see cref="Compare"/> and reports each item as soon as it finishes, with a running count of
    /// finished items. Files are reported before the folder that contains them, and the root comes last. Items
    /// paired as renames after the scan are reported again as revisions.
    /// </summary>
    public static FolderCompareResult CompareStreaming(
        string left,
        string right,
        FolderCompareOptions? options,
        IProgress<FolderCompareProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        // Scan reports one item at a time under its lock, so the counter needs no further synchronisation.
        var completed = 0;
        return Run(
            left,
            right,
            options,
            item => progress.Report(new FolderCompareProgress(item, ++completed)),
            item => progress.Report(new FolderCompareProgress(item, completed) { IsRevision = true }),
            cancellationToken);
    }

    private static FolderCompareResult Run(
        string left,
        string right,
        FolderCompareOptions? options,
        Action<FolderCompareItem>? report,
        Action<FolderCompareItem>? revise,
        CancellationToken cancellationToken)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options ??= new FolderCompareOptions();
        if (options.TimeTolerance < TimeSpan.Zero
            || options.MaxTextFileSize < 0
            || options.MaxTextFileSize > int.MaxValue
            || options.MaxDegreeOfParallelism < 1
            || !Enum.IsDefined(typeof(FolderCompareMethod), options.Method)
            || options.RenameSimilarityThreshold is <= 0 or > 1
            || options.RenameSimilarityThreshold is double.NaN)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var leftRoot = Path.GetFullPath(left);
        var rightRoot = Path.GetFullPath(right);
        using var scan = new Scan(options, report, cancellationToken);
        var root = scan.Visit(leftRoot, rightRoot, string.Empty, depth: 0, hasLeft: true, hasRight: true);
        if (options.DetectRenames && root.Status != FolderCompareStatus.Error)
        {
            var revised = new List<FolderCompareItem>();
            root = FolderRenameDetector.Resolve(root, leftRoot, rightRoot, options, revised, cancellationToken);
            foreach (var item in revised)
            {
                revise?.Invoke(item);
            }
        }

        var items = new List<FolderCompareItem>();
        Flatten(root, items);

        var counts = new Dictionary<FolderCompareStatus, int>();
        foreach (FolderCompareStatus status in Enum.GetValues(typeof(FolderCompareStatus)))
        {
            counts[status] = 0;
        }

        foreach (var item in items)
        {
            counts[item.Status]++;
        }

        return new FolderCompareResult(root, items.AsReadOnly(), new ReadOnlyDictionary<FolderCompareStatus, int>(counts)) { LeftRoot = leftRoot, RightRoot = rightRoot };
    }

    private static void Flatten(FolderCompareItem item, List<FolderCompareItem> items)
    {
        foreach (var child in item.Children)
        {
            items.Add(child);
            Flatten(child, items);
        }
    }

    private sealed class Scan : IDisposable
    {
        private readonly FolderCompareOptions _options;
        private readonly Action<FolderCompareItem>? _report;
        private readonly CancellationToken _token;
        private readonly object _reportLock = new();
        private readonly StringComparer _names;
        private readonly Mask[] _includeFiles;
        private readonly Mask[] _excludeFiles;
        private readonly Mask[] _includeFolders;
        private readonly Mask[] _excludeFolders;
        private readonly ThreadLocal<(byte[] Left, byte[] Right)> _buffers = new(() => (new byte[BufferSize], new byte[BufferSize]));

        public Scan(FolderCompareOptions options, Action<FolderCompareItem>? report, CancellationToken token)
        {
            _options = options;
            _report = report;
            _token = token;
            _names = options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _includeFiles = Mask.CompileAll(options.IncludeFiles, options.CaseSensitive);
            _excludeFiles = Mask.CompileAll(options.ExcludeFiles, options.CaseSensitive);
            _includeFolders = Mask.CompileAll(options.IncludeFolders, options.CaseSensitive);
            _excludeFolders = Mask.CompileAll(options.ExcludeFolders, options.CaseSensitive);
        }

        public void Dispose()
        {
            _buffers.Dispose();
        }

        public FolderCompareItem Visit(
            string left, string right, string relative, int depth, bool hasLeft, bool hasRight,
            FileSystemInfo? leftEntry = null, FileSystemInfo? rightEntry = null)
        {
            _token.ThrowIfCancellationRequested();
            var leftMeta = Missing;
            var rightMeta = Missing;
            var kind = FolderItemKind.Folder;

            FolderCompareItem Finish(FolderCompareStatus status, long? differences = null, string? error = null, IReadOnlyList<FolderCompareItem>? children = null)
            {
                var item = new FolderCompareItem(relative, kind, status, leftMeta, rightMeta, differences, error, children ?? Array.Empty<FolderCompareItem>());
                lock (_reportLock)
                {
                    _report?.Invoke(item);
                }

                return item;
            }

            try
            {
                leftMeta = Metadata(left, hasLeft, leftEntry);
                rightMeta = Metadata(right, hasRight, rightEntry);
                var present = leftMeta.Exists ? leftMeta : rightMeta;
                kind = KindOf(present);
                var isFolder = (present.Attributes & FileAttributes.Directory) != 0;

                if (depth == 0 && !isFolder)
                {
                    return Finish(FolderCompareStatus.Error, error: "Comparison roots must be directories.");
                }

                if (depth > 0 && IsFiltered(relative, isFolder))
                {
                    return Finish(FolderCompareStatus.Skipped);
                }

                var status = !leftMeta.Exists ? FolderCompareStatus.RightOnly
                    : !rightMeta.Exists ? FolderCompareStatus.LeftOnly
                    : FolderCompareStatus.Identical;

                if (leftMeta.Exists && rightMeta.Exists && KindOf(leftMeta) != KindOf(rightMeta))
                {
                    return Finish(FolderCompareStatus.TypeConflict);
                }

                if (kind == FolderItemKind.Link && !_options.FollowSymlinks)
                {
                    // Link targets are not readable on netstandard2.0, so two links cannot be proven identical.
                    return Finish(status == FolderCompareStatus.Identical ? FolderCompareStatus.Different : status);
                }

                if (isFolder)
                {
                    return VisitFolder(left, right, relative, depth, status, leftMeta.Exists, rightMeta.Exists, Finish);
                }

                if (status != FolderCompareStatus.Identical)
                {
                    // Enumeration metadata is a snapshot; a one-sided entry may have vanished since it was listed.
                    File.GetAttributes(leftMeta.Exists ? left : right);
                    return Finish(status);
                }

                var (equal, differences) = CompareFiles(left, right, leftMeta, rightMeta);

                if (_options.Method is not (FolderCompareMethod.Existence or FolderCompareMethod.Size or FolderCompareMethod.Date or FolderCompareMethod.DateAndSize))
                {
                    // An open handle survives deletion; check existence after reading contents.
                    File.GetAttributes(left);
                    File.GetAttributes(right);
                }

                return Finish(equal ? FolderCompareStatus.Identical : FolderCompareStatus.Different, differences);
            }
            catch (IOException ex)
            {
                return Finish(FolderCompareStatus.Error, error: ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Finish(FolderCompareStatus.Error, error: ex.Message);
            }
        }

        private FolderCompareItem VisitFolder(
            string left,
            string right,
            string relative,
            int depth,
            FolderCompareStatus status,
            bool leftExists,
            bool rightExists,
            Func<FolderCompareStatus, long?, string?, IReadOnlyList<FolderCompareItem>?, FolderCompareItem> finish)
        {
            if (depth >= MaxDepth)
            {
                return finish(FolderCompareStatus.Error, null, $"Folder nesting (or a link cycle) exceeds {MaxDepth} levels.", null);
            }

            // Non-recursive: subfolders are listed by existence only.
            if (depth > 0 && !_options.Recursive)
            {
                return finish(status, null, null, null);
            }

            var leftEntries = Entries(left, leftExists);
            var rightEntries = Entries(right, rightExists);
            var names = leftEntries.Keys.Concat(rightEntries.Keys).Distinct(_names).OrderBy(n => n, _names).ToArray();
            var children = new FolderCompareItem[names.Length];

            void VisitChild(int i)
            {
                var name = names[i];
                var hasLeft = leftEntries.TryGetValue(name, out var leftEntry);
                var hasRight = rightEntries.TryGetValue(name, out var rightEntry);
                children[i] = Visit(
                    leftEntry?.FullName ?? Path.Combine(left, name),
                    rightEntry?.FullName ?? Path.Combine(right, name),
                    relative.Length == 0 ? name : relative + "/" + name,
                    depth + 1,
                    hasLeft,
                    hasRight,
                    leftEntry,
                    rightEntry);
            }

            // Only the top level runs in parallel, which keeps the total concurrency bounded.
            if (depth == 0 && _options.MaxDegreeOfParallelism > 1)
            {
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism, CancellationToken = _token };
                Parallel.For(0, names.Length, parallel, VisitChild);
            }
            else
            {
                for (var i = 0; i < names.Length; i++)
                {
                    VisitChild(i);
                }
            }

            // A folder present on both sides is identical only if everything below it is; an unreadable child
            // makes it different rather than failing the whole tree.
            if (status == FolderCompareStatus.Identical
                && children.Any(c => c.Status is not (FolderCompareStatus.Identical or FolderCompareStatus.Skipped)))
            {
                status = FolderCompareStatus.Different;
            }

            return finish(status, null, null, Array.AsReadOnly(children));
        }

        private static FileMeta Metadata(string path, bool exists, FileSystemInfo? entry)
        {
            if (!exists)
            {
                return Missing;
            }

            var information = entry ?? new FileInfo(path);
            var attributes = information.Attributes;
            if ((int)attributes == -1)
            {
                // FileSystemInfo uses -1 for an absent entry; retain the IO error rather than treating it as a link.
                attributes = File.GetAttributes(path);
            }

            var size = (attributes & FileAttributes.Directory) == 0 && information is FileInfo file ? file.Length : 0;
            return new FileMeta(size, information.LastWriteTimeUtc, attributes, true);
        }

        private static FolderItemKind KindOf(FileMeta meta) =>
            (meta.Attributes & FileAttributes.ReparsePoint) != 0 ? FolderItemKind.Link
            : (meta.Attributes & FileAttributes.Directory) != 0 ? FolderItemKind.Folder
            : FolderItemKind.File;

        private Dictionary<string, FileSystemInfo> Entries(string path, bool exists)
        {
            var entries = new Dictionary<string, FileSystemInfo>(_names);
            if (!exists)
            {
                return entries;
            }

            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                _token.ThrowIfCancellationRequested();
                var name = entry.Name;
                if (entries.ContainsKey(name))
                {
                    throw new IOException("Two entries have the same name under the selected case sensitivity: " + name);
                }

                entries.Add(name, entry);
            }

            return entries;
        }

        private bool IsFiltered(string relativePath, bool isFolder)
        {
            var name = relativePath.Substring(relativePath.LastIndexOf('/') + 1);
            if (_options.Filter is { } filter
                && !(isFolder ? filter.IsFolderIncluded(relativePath) : filter.IsFileIncluded(name)))
            {
                return true;
            }

            if (isFolder && _options.DefaultExcludes && NoiseFolders.Contains(name, _names))
            {
                return true;
            }

            var include = isFolder ? _includeFolders : _includeFiles;
            var exclude = isFolder ? _excludeFolders : _excludeFiles;
            return (include.Length != 0 && !include.Any(m => m.Matches(relativePath, name)))
                || exclude.Any(m => m.Matches(relativePath, name));
        }

        private (bool Equal, long? Differences) CompareFiles(string left, string right, FileMeta leftMeta, FileMeta rightMeta)
        {
            var sizeEqual = leftMeta.Size == rightMeta.Size;
            var dateEqual = (leftMeta.LastWriteUtc - rightMeta.LastWriteUtc).Duration() <= _options.TimeTolerance;
            switch (_options.Method)
            {
                case FolderCompareMethod.Existence:
                    return (true, null);
                case FolderCompareMethod.Size:
                    return (sizeEqual, null);
                case FolderCompareMethod.Hash when !sizeEqual:
                    return (false, null);
                case FolderCompareMethod.Hash:
                    return (XxHash64.ComputeFile(left, _token) == XxHash64.ComputeFile(right, _token), null);
                case FolderCompareMethod.Date:
                    return (dateEqual, null);
                case FolderCompareMethod.DateAndSize:
                    return (dateEqual && sizeEqual, null);
                case FolderCompareMethod.QuickContents when !sizeEqual:
                    return (false, null);
                case FolderCompareMethod.QuickContents when dateEqual:
                    return (true, 0);
            }

            using var leftStream = Open(left);
            using var rightStream = Open(right);
            if (BytesEqual(leftStream, rightStream))
            {
                return (true, 0);
            }

            // Full contents: bytes differ, but ignore options (whitespace, case, line endings…) may still make
            // two text files equal.
            if (_options.Method != FolderCompareMethod.FullContents
                || leftStream.Length > _options.MaxTextFileSize
                || rightStream.Length > _options.MaxTextFileSize
                || !LooksLikeText(leftStream)
                || !LooksLikeText(rightStream))
            {
                return (false, null);
            }

            var leftText = TextFileReader.Decode(ReadAll(leftStream)).Text;
            var rightText = TextFileReader.Decode(ReadAll(rightStream)).Text;
            var diff = TextComparer.Compare(TextDocument.Parse(leftText), TextDocument.Parse(rightText), _options.TextOptions, _token);
            return (diff.AreEqual, diff.DifferenceCount);
        }

        private static FileStream Open(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);

        private bool BytesEqual(Stream left, Stream right)
        {
            var (a, b) = _buffers.Value;
            var equal = true;
            while (true)
            {
                var countA = Fill(left, a);
                var countB = Fill(right, b);
                if (countA != countB)
                {
                    equal = false;
                }
                else
                {
                    for (var i = 0; i < countA; i++)
                    {
                        if (a[i] != b[i])
                        {
                            equal = false;
                            break;
                        }
                    }
                }

                // Fill stops short only at EOF; both streams have already been drained in this case.
                if ((!equal && _options.StopAtFirstDifference) || (countA < a.Length && countB < b.Length))
                {
                    return equal;
                }
            }
        }

        private int Fill(Stream stream, byte[] buffer)
        {
            var count = 0;
            while (count < buffer.Length)
            {
                _token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            return count;
        }

        // Same heuristic as the editor: a NUL byte near the start means binary, unless a UTF-16/32 byte order mark says otherwise.
        private bool LooksLikeText(Stream stream)
        {
            stream.Position = 0;
            var probe = new byte[TextProbeSize];
            var count = Fill(stream, probe);
            var hasUnicodeBom = (count >= 2 && ((probe[0] == 0xFF && probe[1] == 0xFE) || (probe[0] == 0xFE && probe[1] == 0xFF)))
                || (count >= 4 && probe[0] == 0 && probe[1] == 0 && probe[2] == 0xFE && probe[3] == 0xFF);
            return hasUnicodeBom || Array.IndexOf(probe, (byte)0, 0, count) < 0;
        }

        private byte[] ReadAll(Stream stream)
        {
            stream.Position = 0;
            var bytes = new byte[stream.Length];
            var count = Fill(stream, bytes);
            if (count == bytes.Length)
            {
                return bytes;
            }

            Array.Resize(ref bytes, count);
            return bytes;
        }
    }

    /// <summary>A file or folder glob: <c>*</c> and <c>?</c> stay within a path segment, <c>**</c> crosses segments.</summary>
    private sealed class Mask
    {
        private readonly Regex _regex;
        private readonly bool _matchesPath;

        private Mask(string mask, bool caseSensitive)
        {
            mask = mask.Replace('\\', '/');

            // Masks with a slash match the relative path; plain masks match the entry name.
            _matchesPath = mask.IndexOf('/') >= 0;
            var pattern = Regex.Escape(mask)
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]");
            var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            _regex = new Regex(@"\A" + pattern + @"\z", options);
        }

        public static Mask[] CompileAll(IReadOnlyList<string> masks, bool caseSensitive) =>
            masks.Select(m => new Mask(m, caseSensitive)).ToArray();

        public bool Matches(string relativePath, string name) => _regex.IsMatch(_matchesPath ? relativePath : name);
    }
}
