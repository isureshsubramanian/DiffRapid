using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace DiffRapid.Core.Structured;

/// <summary>How a node differs between the two files.</summary>
public enum StructuredChangeKind
{
    /// <summary>The node exists only in the right file.</summary>
    Added,

    /// <summary>The node exists only in the left file.</summary>
    Removed,

    /// <summary>The node's value or kind differs.</summary>
    Changed,

    /// <summary>The node is in both files but in a different order among its siblings (only when order matters).</summary>
    Moved,

    /// <summary>An element's identity (such as a .resx data name) differs but its content is the same.</summary>
    Renamed,
}

/// <summary>Options for <see cref="StructuredComparer"/>.</summary>
public sealed record StructuredCompareOptions
{
    /// <summary>The format of both files; <see cref="StructuredFormat.Auto"/> picks it from the file name or content.</summary>
    public StructuredFormat Format { get; init; } = StructuredFormat.Auto;

    /// <summary>
    /// A property that identifies the objects inside JSON arrays and YAML sequences. Null finds one per list: id, name
    /// or key when every item has a unique value for it; otherwise the items are matched by position.
    /// </summary>
    public string? ArrayKey { get; init; }

    /// <summary>When false, siblings that are in a different order are reported as <see cref="StructuredChangeKind.Moved"/>.</summary>
    public bool IgnoreOrder { get; init; } = true;
}

/// <summary>One difference between two structured files, with the lines it covers on each side.</summary>
/// <param name="Kind">How the node differs.</param>
/// <param name="Left">The node in the left file, or null when added.</param>
/// <param name="Right">The node in the right file, or null when removed.</param>
public sealed record StructuredDifference(StructuredChangeKind Kind, StructuredNode? Left, StructuredNode? Right)
{
    /// <summary>The node's path; the left path for removed and renamed nodes.</summary>
    public string Path => (Left ?? Right)!.Path;

    /// <summary>The lines in the left file, or null when added.</summary>
    public StructuredLineSpan? LeftSpan => Left?.Span;

    /// <summary>The lines in the right file, or null when removed.</summary>
    public StructuredLineSpan? RightSpan => Right?.Span;
}

/// <summary>The result of comparing two structured files.</summary>
/// <param name="Format">The format both files were read as.</param>
/// <param name="Left">The left tree.</param>
/// <param name="Right">The right tree.</param>
/// <param name="Differences">The differences in document order.</param>
public sealed record StructuredComparison(
    StructuredFormat Format,
    StructuredNode Left,
    StructuredNode Right,
    IReadOnlyList<StructuredDifference> Differences)
{
    /// <summary>True when the files have the same structure and values.</summary>
    public bool IsIdentical => Differences.Count == 0;
}

/// <summary>
/// Compares two structured trees by key: children are matched by <see cref="StructuredNode.Key"/> whatever their
/// order, so reordered properties or items are not differences unless order is asked to matter.
/// </summary>
public static class StructuredComparer
{
    /// <summary>Reads and compares two texts.</summary>
    /// <param name="leftText">The left file's text.</param>
    /// <param name="rightText">The right file's text.</param>
    /// <param name="options">The format and matching options.</param>
    /// <param name="fileName">A file name used to choose the format when it is <see cref="StructuredFormat.Auto"/>.</param>
    /// <param name="cancellationToken">Stops a long comparison.</param>
    /// <exception cref="StructuredFormatException">A text cannot be read.</exception>
    public static StructuredComparison Compare(
        string leftText,
        string rightText,
        StructuredCompareOptions? options = null,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredCompareOptions();
        var format = options.Format == StructuredFormat.Auto ? StructuredReader.DetectFormat(fileName, leftText) : options.Format;
        var left = StructuredReader.Read(leftText, format, options.ArrayKey, cancellationToken);
        var right = StructuredReader.Read(rightText, format, options.ArrayKey, cancellationToken);
        return new StructuredComparison(format, left, right, Compare(left, right, options.IgnoreOrder, cancellationToken));
    }

    /// <summary>Compares two trees read with the same reader.</summary>
    /// <param name="left">The left tree.</param>
    /// <param name="right">The right tree.</param>
    /// <param name="ignoreOrder">When false, reordered siblings are reported as moved.</param>
    /// <param name="cancellationToken">Stops a long comparison.</param>
    public static IReadOnlyList<StructuredDifference> Compare(
        StructuredNode left,
        StructuredNode right,
        bool ignoreOrder = true,
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

        var differences = new List<StructuredDifference>();
        CompareNodes(left, right, ignoreOrder, differences, cancellationToken);
        return differences;
    }

    private static void CompareNodes(StructuredNode left, StructuredNode right, bool ignoreOrder, List<StructuredDifference> differences, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sameKind = left.Kind == right.Kind;
        if (!sameKind || !string.Equals(left.Value, right.Value, StringComparison.Ordinal))
        {
            differences.Add(new StructuredDifference(StructuredChangeKind.Changed, left, right));

            // An element whose text changed still has its attributes compared; anything else stops here.
            if (!sameKind || IsScalar(left.Kind))
            {
                return;
            }
        }

        CompareChildren(left, right, ignoreOrder, differences, cancellationToken);
    }

    private static void CompareChildren(StructuredNode left, StructuredNode right, bool ignoreOrder, List<StructuredDifference> differences, CancellationToken cancellationToken)
    {
        var rightByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < right.Children.Count; i++)
        {
            rightByKey[right.Children[i].Key] = i;
        }

        var matchedRight = new bool[right.Children.Count];
        var pairs = new List<(StructuredNode Left, StructuredNode Right, int RightIndex)>();
        var removed = new List<StructuredNode>();
        foreach (var child in left.Children)
        {
            if (rightByKey.TryGetValue(child.Key, out var index))
            {
                matchedRight[index] = true;
                pairs.Add((child, right.Children[index], index));
            }
            else
            {
                removed.Add(child);
            }
        }

        var added = right.Children.Where((child, index) => !matchedRight[index]).ToList();
        var renamed = PairRenames(removed, added);
        var moved = ignoreOrder ? new HashSet<StructuredNode>() : FindMoved(pairs);
        foreach (var (leftChild, rightChild, _) in pairs)
        {
            if (moved.Contains(leftChild))
            {
                differences.Add(new StructuredDifference(StructuredChangeKind.Moved, leftChild, rightChild));
            }

            CompareNodes(leftChild, rightChild, ignoreOrder, differences, cancellationToken);
        }

        foreach (var child in removed)
        {
            var kind = renamed.TryGetValue(child, out var renamedTo) ? StructuredChangeKind.Renamed : StructuredChangeKind.Removed;
            differences.Add(new StructuredDifference(kind, child, renamedTo));
        }

        var renamedTargets = new HashSet<StructuredNode>(renamed.Values);
        foreach (var child in added.Where(child => !renamedTargets.Contains(child)))
        {
            differences.Add(new StructuredDifference(StructuredChangeKind.Added, null, child));
        }
    }

    /// <summary>
    /// Pairs removed and added elements that carry an identity attribute and are equal apart from it, such as a
    /// .resx data entry whose name changed.
    /// </summary>
    private static Dictionary<StructuredNode, StructuredNode> PairRenames(List<StructuredNode> removed, List<StructuredNode> added)
    {
        var renames = new Dictionary<StructuredNode, StructuredNode>();
        if (removed.Count == 0 || added.Count == 0)
        {
            return renames;
        }

        var candidates = new Dictionary<string, Queue<StructuredNode>>(StringComparer.Ordinal);
        foreach (var node in added.Where(node => node.IdentityAttribute is not null))
        {
            var signature = Signature(node);
            if (!candidates.TryGetValue(signature, out var queue))
            {
                queue = new Queue<StructuredNode>();
                candidates[signature] = queue;
            }

            queue.Enqueue(node);
        }

        foreach (var node in removed.Where(node => node.IdentityAttribute is not null))
        {
            if (candidates.TryGetValue(Signature(node), out var queue) && queue.Count > 0)
            {
                renames[node] = queue.Dequeue();
            }
        }

        return renames;
    }

    private static string Signature(StructuredNode node)
    {
        var writer = new StringBuilder();
        AppendSignature(writer, node, node.IdentityAttribute);
        return writer.ToString();
    }

    private static void AppendSignature(StringBuilder builder, StructuredNode node, string? skippedAttribute)
    {
        builder.Append(node.Kind).Append('<').Append(node.Name).Append('=').Append(node.Value?.Length ?? -1).Append(':').Append(node.Value).Append('{');
        foreach (var child in node.Children.OrderBy(child => child.Key, StringComparer.Ordinal))
        {
            var isIdentity = child.Kind == StructuredNodeKind.Attribute && skippedAttribute is not null
                && string.Equals(child.Name, skippedAttribute, StringComparison.Ordinal);
            if (!isIdentity)
            {
                builder.Append(child.Key.Length).Append(':').Append(child.Key);
                AppendSignature(builder, child, null);
            }
        }

        builder.Append('}');
    }

    /// <summary>
    /// Finds matched children whose relative order changed: the longest run that keeps its order stays put and
    /// the rest moved. Attributes and items keyed by position never move.
    /// </summary>
    private static HashSet<StructuredNode> FindMoved(List<(StructuredNode Left, StructuredNode Right, int RightIndex)> pairs)
    {
        var candidates = pairs.Where(pair => pair.Left.Kind != StructuredNodeKind.Attribute && !pair.Left.IsKeyedByIndex).ToList();
        var stays = LongestIncreasingRun(candidates.Select(pair => pair.RightIndex).ToList());
        var moved = new HashSet<StructuredNode>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!stays.Contains(i))
            {
                moved.Add(candidates[i].Left);
            }
        }

        return moved;
    }

    /// <summary>Returns the positions of a longest strictly increasing subsequence, in O(n log n).</summary>
    private static HashSet<int> LongestIncreasingRun(List<int> values)
    {
        var tails = new List<int>();
        var previous = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (values[tails[middle]] < values[i])
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[low] = i;
            }
        }

        var result = new HashSet<int>();
        for (var index = tails.Count > 0 ? tails[tails.Count - 1] : -1; index >= 0; index = previous[index])
        {
            result.Add(index);
        }

        return result;
    }

    private static bool IsScalar(StructuredNodeKind kind) =>
        kind is StructuredNodeKind.String or StructuredNodeKind.Number or StructuredNodeKind.Boolean or StructuredNodeKind.Null
            or StructuredNodeKind.Attribute or StructuredNodeKind.Text;
}
