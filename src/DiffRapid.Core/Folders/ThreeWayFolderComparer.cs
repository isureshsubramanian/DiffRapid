using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DiffRapid.Core.Folders;

/// <summary>The outcome of an entry in a three-way folder comparison, relative to the base.</summary>
public enum ThreeWayFolderStatus
{
    /// <summary>The same in all three folders.</summary>
    Unchanged,

    /// <summary>Changed on the left only.</summary>
    ChangedLeft,

    /// <summary>Changed on the right only.</summary>
    ChangedRight,

    /// <summary>Changed on both sides in the same way.</summary>
    ChangedBothIdentically,

    /// <summary>A folder whose contents changed on both sides without any conflicting entry.</summary>
    ChangedBoth,

    /// <summary>Changed on both sides in different ways, or removed on one side and changed on the other.</summary>
    Conflict,

    /// <summary>Not in the base; added on the left only.</summary>
    AddedLeft,

    /// <summary>Not in the base; added on the right only.</summary>
    AddedRight,

    /// <summary>Not in the base; added on both sides with the same contents.</summary>
    AddedBothIdentically,

    /// <summary>Removed on the left; the right still matches the base.</summary>
    RemovedLeft,

    /// <summary>Removed on the right; the left still matches the base.</summary>
    RemovedRight,

    /// <summary>Removed on both sides.</summary>
    RemovedBoth,

    /// <summary>Excluded by a filter.</summary>
    Skipped,

    /// <summary>Could not be read.</summary>
    Error,
}

/// <summary>An entry in a three-way folder comparison. Directory children are complete.</summary>
public sealed record ThreeWayFolderItem(
    string RelativePath,
    FolderItemKind Kind,
    ThreeWayFolderStatus Status,
    FileMeta Base,
    FileMeta Left,
    FileMeta Right,
    string? Error,
    IReadOnlyList<ThreeWayFolderItem> Children);

/// <summary>The completed three-way tree and ordered flat list. Counts exclude the synthetic root.</summary>
public sealed record ThreeWayFolderResult(
    ThreeWayFolderItem Root,
    IReadOnlyList<ThreeWayFolderItem> Items,
    IReadOnlyDictionary<ThreeWayFolderStatus, int> Counts)
{
    /// <summary>Absolute root of the base tree.</summary>
    public string BaseRoot { get; init; } = string.Empty;

    /// <summary>Absolute root of the left tree.</summary>
    public string LeftRoot { get; init; } = string.Empty;

    /// <summary>Absolute root of the right tree.</summary>
    public string RightRoot { get; init; } = string.Empty;
}

/// <summary>
/// Compares a left and a right folder against their common base. It runs <see cref="FolderComparer"/> for
/// base/left, base/right and left/right with the same options, then classifies each path from the three results.
/// </summary>
public static class ThreeWayFolderComparer
{
    private static readonly FileMeta Missing = new(0, DateTime.MinValue, 0, false);

    /// <summary>
    /// Scans synchronously. Rename detection is not used here. Cancellation throws
    /// <see cref="OperationCanceledException"/>; IO failures become <see cref="ThreeWayFolderStatus.Error"/> items.
    /// </summary>
    public static ThreeWayFolderResult Compare(
        string baseFolder,
        string left,
        string right,
        FolderCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (baseFolder is null)
        {
            throw new ArgumentNullException(nameof(baseFolder));
        }

        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options = (options ?? new FolderCompareOptions()) with { DetectRenames = false };
        var baseLeft = FolderComparer.Compare(baseFolder, left, options, null, cancellationToken);
        var baseRight = FolderComparer.Compare(baseFolder, right, options, null, cancellationToken);
        var leftRight = FolderComparer.Compare(left, right, options, null, cancellationToken);

        var merger = new Merger(options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase, cancellationToken);
        var root = merger.Merge(string.Empty, baseLeft.Root, baseRight.Root, leftRight.Root);

        var items = new List<ThreeWayFolderItem>();
        Flatten(root, items);
        var counts = new Dictionary<ThreeWayFolderStatus, int>();
        foreach (ThreeWayFolderStatus status in Enum.GetValues(typeof(ThreeWayFolderStatus)))
        {
            counts[status] = 0;
        }

        foreach (var item in items)
        {
            counts[item.Status]++;
        }

        return new ThreeWayFolderResult(root, items.AsReadOnly(), new ReadOnlyDictionary<ThreeWayFolderStatus, int>(counts))
        {
            BaseRoot = baseLeft.LeftRoot,
            LeftRoot = baseLeft.RightRoot,
            RightRoot = baseRight.RightRoot,
        };
    }

    private static void Flatten(ThreeWayFolderItem item, List<ThreeWayFolderItem> items)
    {
        foreach (var child in item.Children)
        {
            items.Add(child);
            Flatten(child, items);
        }
    }

    private sealed class Merger
    {
        private readonly StringComparer _names;
        private readonly CancellationToken _token;

        public Merger(StringComparer names, CancellationToken token)
        {
            _names = names;
            _token = token;
        }

        /// <summary>Merges the nodes for one path from the three pairwise trees; a node is null when neither of its folders has the path.</summary>
        public ThreeWayFolderItem Merge(string relative, FolderCompareItem? baseLeft, FolderCompareItem? baseRight, FolderCompareItem? leftRight)
        {
            _token.ThrowIfCancellationRequested();
            var children = MergeChildren(relative, baseLeft, baseRight, leftRight);
            var baseMeta = Pick(baseLeft?.Left, baseRight?.Left);
            var leftMeta = Pick(baseLeft?.Right, leftRight?.Left);
            var rightMeta = Pick(baseRight?.Right, leftRight?.Right);
            var kind = (leftRight ?? baseLeft ?? baseRight)!.Kind;
            var error = new[] { baseLeft, baseRight, leftRight }.FirstOrDefault(n => n?.Status == FolderCompareStatus.Error)?.Error;
            var status = error is not null
                ? ThreeWayFolderStatus.Error
                : Classify(baseLeft, baseRight, leftRight, baseMeta.Exists, leftMeta.Exists, rightMeta.Exists, children);
            return new ThreeWayFolderItem(relative, kind, status, baseMeta, leftMeta, rightMeta, error, children);
        }

        private IReadOnlyList<ThreeWayFolderItem> MergeChildren(string relative, FolderCompareItem? baseLeft, FolderCompareItem? baseRight, FolderCompareItem? leftRight)
        {
            var baseLeftChildren = ByName(baseLeft);
            var baseRightChildren = ByName(baseRight);
            var leftRightChildren = ByName(leftRight);
            var names = baseLeftChildren.Keys
                .Concat(baseRightChildren.Keys)
                .Concat(leftRightChildren.Keys)
                .Distinct(_names)
                .OrderBy(n => n, _names)
                .ToArray();
            if (names.Length == 0)
            {
                return Array.Empty<ThreeWayFolderItem>();
            }

            var children = new ThreeWayFolderItem[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i];
                children[i] = Merge(
                    relative.Length == 0 ? name : relative + "/" + name,
                    baseLeftChildren.TryGetValue(name, out var a) ? a : null,
                    baseRightChildren.TryGetValue(name, out var b) ? b : null,
                    leftRightChildren.TryGetValue(name, out var c) ? c : null);
            }

            return Array.AsReadOnly(children);
        }

        private Dictionary<string, FolderCompareItem> ByName(FolderCompareItem? node)
        {
            var children = new Dictionary<string, FolderCompareItem>(_names);
            if (node is null)
            {
                return children;
            }

            foreach (var child in node.Children)
            {
                children[child.RelativePath.Substring(child.RelativePath.LastIndexOf('/') + 1)] = child;
            }

            return children;
        }

        private static ThreeWayFolderStatus Classify(
            FolderCompareItem? baseLeft,
            FolderCompareItem? baseRight,
            FolderCompareItem? leftRight,
            bool inBase,
            bool inLeft,
            bool inRight,
            IReadOnlyList<ThreeWayFolderItem> children)
        {
            if (new[] { baseLeft, baseRight, leftRight }.Any(n => n?.Status == FolderCompareStatus.Skipped))
            {
                return ThreeWayFolderStatus.Skipped;
            }

            if (!inBase)
            {
                return Added(inLeft, inRight, leftRight, children);
            }

            if (!inLeft && !inRight)
            {
                return ThreeWayFolderStatus.RemovedBoth;
            }

            if (!inLeft)
            {
                return Same(baseRight) ? ThreeWayFolderStatus.RemovedLeft : ThreeWayFolderStatus.Conflict;
            }

            if (!inRight)
            {
                return Same(baseLeft) ? ThreeWayFolderStatus.RemovedRight : ThreeWayFolderStatus.Conflict;
            }

            return Changed(baseLeft, baseRight, leftRight, children);
        }

        private static ThreeWayFolderStatus Added(bool inLeft, bool inRight, FolderCompareItem? leftRight, IReadOnlyList<ThreeWayFolderItem> children)
        {
            if (!inRight)
            {
                return ThreeWayFolderStatus.AddedLeft;
            }

            if (!inLeft)
            {
                return ThreeWayFolderStatus.AddedRight;
            }

            if (Same(leftRight))
            {
                return ThreeWayFolderStatus.AddedBothIdentically;
            }

            return IsFolderPair(leftRight) ? Aggregate(children) : ThreeWayFolderStatus.Conflict;
        }

        private static ThreeWayFolderStatus Changed(FolderCompareItem? baseLeft, FolderCompareItem? baseRight, FolderCompareItem? leftRight, IReadOnlyList<ThreeWayFolderItem> children)
        {
            var leftSame = Same(baseLeft);
            var rightSame = Same(baseRight);
            if (leftSame && rightSame)
            {
                return ThreeWayFolderStatus.Unchanged;
            }

            if (leftSame)
            {
                return ThreeWayFolderStatus.ChangedRight;
            }

            if (rightSame)
            {
                return ThreeWayFolderStatus.ChangedLeft;
            }

            if (Same(leftRight))
            {
                return ThreeWayFolderStatus.ChangedBothIdentically;
            }

            var allFolders = IsFolderPair(baseLeft) && IsFolderPair(baseRight) && IsFolderPair(leftRight);
            return allFolders ? Aggregate(children) : ThreeWayFolderStatus.Conflict;
        }

        // A folder changed on both sides conflicts only if something inside it does.
        private static ThreeWayFolderStatus Aggregate(IReadOnlyList<ThreeWayFolderItem> children) =>
            children.Any(c => c.Status is ThreeWayFolderStatus.Conflict or ThreeWayFolderStatus.Error)
                ? ThreeWayFolderStatus.Conflict
                : ThreeWayFolderStatus.ChangedBoth;

        private static bool Same(FolderCompareItem? node) => node?.Status == FolderCompareStatus.Identical;

        private static bool IsFolderPair(FolderCompareItem? node) =>
            node is not null && node.Kind == FolderItemKind.Folder && node.Status != FolderCompareStatus.TypeConflict;

        private static FileMeta Pick(FileMeta? first, FileMeta? second) =>
            first is { Exists: true } ? first : second is { Exists: true } ? second : Missing;
    }
}
