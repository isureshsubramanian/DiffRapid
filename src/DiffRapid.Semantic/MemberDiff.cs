using System.Collections.Generic;
using System.Linq;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Semantic;

/// <summary>How a member differs between the left and the right file.</summary>
public enum MemberDiffStatus
{
    /// <summary>Same identity, same text, same position.</summary>
    Unchanged,

    /// <summary>Same identity, different text.</summary>
    Changed,

    /// <summary>Only on the right.</summary>
    Added,

    /// <summary>Only on the left.</summary>
    Removed,

    /// <summary>The same member at a different position among its siblings, or in a different type.</summary>
    Moved,

    /// <summary>A different name with the same or a very similar body.</summary>
    Renamed,
}

/// <summary>Options for <see cref="MemberMatcher"/>.</summary>
public sealed record MemberMatchOptions
{
    public static readonly MemberMatchOptions Default = new();

    /// <summary>Differences that do not make a member Changed.</summary>
    public CSharpIgnoreOptions Ignore { get; init; }

    /// <summary>Minimum token similarity (0..1) for a rename or a move to another type. Default 0.8.</summary>
    public double SimilarityThreshold { get; init; } = 0.8;

    /// <summary>
    /// Upper bound on the left-by-right pairs scored when looking for renames among one set of siblings; above it
    /// only members with identical bodies are paired. Keeps the cost bounded for files with many unmatched members.
    /// </summary>
    public int MaxSimilarityPairs { get; init; } = 10_000;

    /// <summary>Options for the per-member text diff of changed members.</summary>
    public CompareOptions TextOptions { get; init; } = CompareOptions.Default;
}

/// <summary>One entry of the member comparison tree.</summary>
public sealed class MemberDiff
{
    private readonly List<MemberDiff> _children = [];

    internal MemberDiff(MemberDiffStatus status, CSharpMember? left, CSharpMember? right)
    {
        Status = status;
        Left = left;
        Right = right;
    }

    public MemberDiffStatus Status { get; internal set; }

    /// <summary>The member in the left file; null when Added.</summary>
    public CSharpMember? Left { get; }

    /// <summary>The member in the right file; null when Removed.</summary>
    public CSharpMember? Right { get; }

    public CSharpMemberKind Kind => (Right ?? Left)!.Kind;

    /// <summary>The right signature, or the left one for a removed member.</summary>
    public string Signature => (Right ?? Left)!.Signature;

    /// <summary>True when the compared text differs; also set for moved and renamed members whose body changed.</summary>
    public bool TextChanged { get; internal set; }

    /// <summary>The line diff of the compared text when <see cref="TextChanged"/> is set.</summary>
    public TextDiffResult? TextDiff { get; internal set; }

    /// <summary>Token similarity of the two bodies for renamed and moved members; 1 otherwise when matched.</summary>
    public double Similarity { get; internal set; } = 1;

    /// <summary>Entries for the members inside this namespace or type, in display order.</summary>
    public IReadOnlyList<MemberDiff> Children => _children;

    /// <summary>True when this entry or any entry below it is not Unchanged.</summary>
    public bool HasDifferences => Status != MemberDiffStatus.Unchanged || _children.Any(child => child.HasDifferences);

    internal List<MemberDiff> MutableChildren => _children;

    public override string ToString() => Status + " " + Kind + " " + Signature;
}

/// <summary>The result of <see cref="MemberMatcher.Compare(string, string, MemberMatchOptions?, System.Threading.CancellationToken)"/>.</summary>
public sealed class MemberDiffResult
{
    internal MemberDiffResult(
        IReadOnlyList<MemberDiff> roots,
        IReadOnlyList<CSharpMember> leftMembers,
        IReadOnlyList<CSharpMember> rightMembers,
        bool leftHasErrors,
        bool rightHasErrors)
    {
        Roots = roots;
        LeftMembers = leftMembers;
        RightMembers = rightMembers;
        LeftHasErrors = leftHasErrors;
        RightHasErrors = rightHasErrors;
    }

    /// <summary>The top-level entries; nested members are in <see cref="MemberDiff.Children"/>.</summary>
    public IReadOnlyList<MemberDiff> Roots { get; }

    /// <summary>The top-level members of the left file.</summary>
    public IReadOnlyList<CSharpMember> LeftMembers { get; }

    /// <summary>The top-level members of the right file.</summary>
    public IReadOnlyList<CSharpMember> RightMembers { get; }

    /// <summary>The left file has syntax errors; members were matched on a best-effort parse.</summary>
    public bool LeftHasErrors { get; }

    /// <summary>The right file has syntax errors; members were matched on a best-effort parse.</summary>
    public bool RightHasErrors { get; }

    /// <summary>True when every entry is Unchanged.</summary>
    public bool AreEqual => Roots.All(root => !root.HasDifferences);

    /// <summary>All entries, depth first, in display order.</summary>
    public IEnumerable<MemberDiff> Flatten()
    {
        var stack = new Stack<MemberDiff>(Roots.Reverse());
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            yield return entry;
            for (var i = entry.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(entry.Children[i]);
            }
        }
    }
}
