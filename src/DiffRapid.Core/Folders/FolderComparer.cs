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
    /// (one at a time).
    /// </summary>
    public static FolderCompareResult Compare(
        string left,
        string right,
        FolderCompareOptions? options = null,
        IProgress<FolderCompareItem>? itemReported = null,
        CancellationToken cancellationToken = default)
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
            || !Enum.IsDefined(typeof(FolderCompareMethod), options.Method))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var scan = new Scan(options, itemReported, cancellationToken);
        var root = scan.Visit(Path.GetFullPath(left), Path.GetFullPath(right), string.Empty, depth: 0, hasLeft: true, hasRight: true);

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

        return new FolderCompareResult(root, items.AsReadOnly(), new ReadOnlyDictionary<FolderCompareStatus, int>(counts));
    }

    private static void Flatten(FolderCompareItem item, List<FolderCompareItem> items)
    {
        foreach (var child in item.Children)
        {
            items.Add(child);
            Flatten(child, items);
        }
    }

    private sealed class Scan
    {
        private readonly FolderCompareOptions _options;
        private readonly IProgress<FolderCompareItem>? _progress;
        private readonly CancellationToken _token;
        private readonly object _reportLock = new();
        private readonly StringComparer _names;
        private readonly Mask[] _includeFiles;
        private readonly Mask[] _excludeFiles;
        private readonly Mask[] _includeFolders;
        private readonly Mask[] _excludeFolders;

        public Scan(FolderCompareOptions options, IProgress<FolderCompareItem>? progress, CancellationToken token)
        {
            _options = options;
            _progress = progress;
            _token = token;
            _names = options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _includeFiles = Mask.CompileAll(options.IncludeFiles, options.CaseSensitive);
            _excludeFiles = Mask.CompileAll(options.ExcludeFiles, options.CaseSensitive);
            _includeFolders = Mask.CompileAll(options.IncludeFolders, options.CaseSensitive);
            _excludeFolders = Mask.CompileAll(options.ExcludeFolders, options.CaseSensitive);
        }

        public FolderCompareItem Visit(string left, string right, string relative, int depth, bool hasLeft, bool hasRight)
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
                    _progress?.Report(item);
                }

                return item;
            }

            try
            {
                leftMeta = Metadata(left, hasLeft);
                rightMeta = Metadata(right, hasRight);
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
                    return Finish(status);
                }

                var (equal, differences) = CompareFiles(left, right, leftMeta, rightMeta);

                // An open handle survives deletion; make sure both entries still exist after reading them.
                File.GetAttributes(left);
                File.GetAttributes(right);
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
                var hasLeft = leftEntries.TryGetValue(name, out var leftPath);
                var hasRight = rightEntries.TryGetValue(name, out var rightPath);
                children[i] = Visit(
                    leftPath ?? Path.Combine(left, name),
                    rightPath ?? Path.Combine(right, name),
                    relative.Length == 0 ? name : relative + "/" + name,
                    depth + 1,
                    hasLeft,
                    hasRight);
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

        private static FileMeta Metadata(string path, bool exists)
        {
            if (!exists)
            {
                return Missing;
            }

            var attributes = File.GetAttributes(path);
            var size = (attributes & FileAttributes.Directory) != 0 ? 0 : new FileInfo(path).Length;
            return new FileMeta(size, File.GetLastWriteTimeUtc(path), attributes, true);
        }

        private static FolderItemKind KindOf(FileMeta meta) =>
            (meta.Attributes & FileAttributes.ReparsePoint) != 0 ? FolderItemKind.Link
            : (meta.Attributes & FileAttributes.Directory) != 0 ? FolderItemKind.Folder
            : FolderItemKind.File;

        private Dictionary<string, string> Entries(string path, bool exists)
        {
            var entries = new Dictionary<string, string>(_names);
            if (!exists)
            {
                return entries;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                _token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
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
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);

        private bool BytesEqual(Stream left, Stream right)
        {
            var a = new byte[BufferSize];
            var b = new byte[BufferSize];
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

                if ((!equal && _options.StopAtFirstDifference) || (countA == 0 && countB == 0))
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
