using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;
using DiffRapid.Semantic.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace DiffRapid.Semantic;

/// <summary>
/// Compares two C# files member by member. Members are matched by identity (kind, name, generic arity and
/// parameter types) within matched namespaces and types; the rest are then paired as renames (same kind, a
/// different name, a similar body) or as moves to another type (same identity, a similar body).
/// </summary>
public static class MemberMatcher
{
    /// <summary>Parses a C# file and returns its top-level members; nested members are in their Children.</summary>
    public static IReadOnlyList<CSharpMember> ListMembers(string text, CancellationToken cancellationToken = default)
    {
        return SourceFile.Parse(text, cancellationToken).Members;
    }

    /// <summary>Compares the members of two C# files. Files with syntax errors are compared on a best-effort parse.</summary>
    public static MemberDiffResult Compare(
        string leftText,
        string rightText,
        MemberMatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (leftText is null)
        {
            throw new ArgumentNullException(nameof(leftText));
        }

        if (rightText is null)
        {
            throw new ArgumentNullException(nameof(rightText));
        }

        var left = SourceFile.Parse(leftText, cancellationToken);
        var right = SourceFile.Parse(rightText, cancellationToken);
        return new Session(left, right, options ?? MemberMatchOptions.Default, cancellationToken).Run();
    }

    /// <summary>A parsed file with its line table and member tree.</summary>
    private sealed class SourceFile
    {
        private SourceFile(string text, LineTable table, SyntaxNode root, List<CSharpMember> members, bool hasErrors)
        {
            Text = text;
            Table = table;
            Root = root;
            Members = members;
            HasErrors = hasErrors;
        }

        public string Text { get; }

        public LineTable Table { get; }

        public SyntaxNode Root { get; }

        public List<CSharpMember> Members { get; }

        public bool HasErrors { get; }

        public static SourceFile Parse(string text, CancellationToken cancellationToken)
        {
            var tree = CSharpSyntaxTree.ParseText(text, CSharpNormalizer.ParseOptions, cancellationToken: cancellationToken);
            var root = tree.GetRoot(cancellationToken);
            var table = LineTable.Create(text);
            var hasErrors = root.ContainsDiagnostics && tree.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error);
            return new SourceFile(text, table, root, MemberCollector.Collect(root, table), hasErrors);
        }

        /// <summary>The compared text, from the start of its first line when only indentation precedes it.</summary>
        public string Fragment(CSharpMember member) => Text.Substring(FragmentStart(member), member.CompareSpan.End - FragmentStart(member));

        /// <summary>Where <see cref="Fragment"/> starts.</summary>
        public int FragmentStart(CSharpMember member)
        {
            var lineStart = Table.Start(member.CompareSpan.StartLine);
            for (var i = lineStart; i < member.CompareSpan.Start; i++)
            {
                if (Text[i] != ' ' && Text[i] != '\t')
                {
                    return member.CompareSpan.Start;
                }
            }

            return lineStart;
        }
    }

    /// <summary>One comparison: matching state and caches.</summary>
    private sealed class Session
    {
        /// <summary>Stands in for a member's name when renamed members are compared.</summary>
        private const string HiddenName = "__member_name__";

        private readonly SourceFile _left;
        private readonly SourceFile _right;
        private readonly MemberMatchOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<CSharpMember, MemberDiff> _leftDiffs = [];
        private readonly Dictionary<CSharpMember, MemberDiff> _rightDiffs = [];
        private readonly List<MemberDiff> _all = [];
        private readonly List<CSharpMember> _unmatchedLeft = [];
        private readonly List<CSharpMember> _unmatchedRight = [];
        private readonly HashSet<CSharpMember> _unmatchedSeen = [];
        private readonly Dictionary<CSharpMember, string> _compareKeys = [];
        private readonly Dictionary<CSharpMember, int[]> _tokens = [];
        private readonly Dictionary<string, int> _tokenIds = new(StringComparer.Ordinal);

        public Session(SourceFile left, SourceFile right, MemberMatchOptions options, CancellationToken cancellationToken)
        {
            _left = left;
            _right = right;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public MemberDiffResult Run()
        {
            MatchLevel(_left.Members, _right.Members);
            MatchMovesAcrossContainers();
            AddLeftovers();
            var roots = BuildTree();
            return new MemberDiffResult(roots, _left.Members, _right.Members, _left.HasErrors, _right.HasErrors);
        }

        /// <summary>Matches the children of one matched pair of containers (or the two files' top levels).</summary>
        private void MatchLevel(IReadOnlyList<CSharpMember> lefts, IReadOnlyList<CSharpMember> rights)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var queues = new Dictionary<string, Queue<CSharpMember>>(StringComparer.Ordinal);
            foreach (var right in rights.Where(member => !_rightDiffs.ContainsKey(member)))
            {
                if (!queues.TryGetValue(right.Key, out var queue))
                {
                    queue = new Queue<CSharpMember>();
                    queues[right.Key] = queue;
                }

                queue.Enqueue(right);
            }

            var pairs = new List<(CSharpMember Left, CSharpMember Right)>();
            var leftRest = new List<CSharpMember>();
            foreach (var left in lefts.Where(member => !_leftDiffs.ContainsKey(member)))
            {
                if (queues.TryGetValue(left.Key, out var queue) && queue.Count > 0)
                {
                    pairs.Add((left, queue.Dequeue()));
                }
                else
                {
                    leftRest.Add(left);
                }
            }

            foreach (var (left, right) in pairs)
            {
                Pair(left, right, MemberDiffStatus.Unchanged, 1);
            }

            MarkReordered(pairs);
            var rightRest = rights.Where(member => !_rightDiffs.ContainsKey(member)).ToList();
            var similar = FindSimilar(leftRest, rightRest);
            foreach (var (left, right, status, similarity) in similar)
            {
                Pair(left, right, status, similarity);
            }

            AddUnmatched(leftRest.Where(member => !_leftDiffs.ContainsKey(member)), _unmatchedLeft);
            AddUnmatched(rightRest.Where(member => !_rightDiffs.ContainsKey(member)), _unmatchedRight);
            foreach (var (left, right) in pairs.Concat(similar.Select(match => (match.Left, match.Right))))
            {
                if (left.IsContainer && right.IsContainer)
                {
                    MatchLevel(left.Children, right.Children);
                }
            }
        }

        /// <summary>Matched siblings outside the longest run that keeps its order were moved.</summary>
        private void MarkReordered(List<(CSharpMember Left, CSharpMember Right)> pairs)
        {
            var inOrder = LongestIncreasingRun(pairs.Select(pair => pair.Right.Index).ToArray());
            for (var i = 0; i < pairs.Count; i++)
            {
                if (!inOrder[i])
                {
                    _leftDiffs[pairs[i].Left].Status = MemberDiffStatus.Moved;
                }
            }
        }

        /// <summary>
        /// Pairs unmatched siblings: a different signature with the same name is a change of signature, a
        /// different name with a similar body is a rename.
        /// </summary>
        private List<(CSharpMember Left, CSharpMember Right, MemberDiffStatus Status, double Similarity)> FindSimilar(
            List<CSharpMember> lefts,
            List<CSharpMember> rights)
        {
            var result = new List<(CSharpMember, CSharpMember, MemberDiffStatus, double)>();
            if (lefts.Count == 0 || rights.Count == 0)
            {
                return result;
            }

            var candidates = (long)lefts.Count * rights.Count <= _options.MaxSimilarityPairs
                ? ScoreAllPairs(lefts, rights)
                : IdenticalBodies(lefts, rights);
            var usedLeft = new HashSet<CSharpMember>();
            var usedRight = new HashSet<CSharpMember>();
            var ordered = candidates
                .OrderByDescending(candidate => candidate.Similarity)
                .ThenBy(candidate => Math.Abs(candidate.Left.Index - candidate.Right.Index));
            foreach (var (left, right, similarity) in ordered)
            {
                if (usedLeft.Contains(left) || usedRight.Contains(right))
                {
                    continue;
                }

                usedLeft.Add(left);
                usedRight.Add(right);
                var status = left.Name == right.Name ? MemberDiffStatus.Unchanged : MemberDiffStatus.Renamed;
                result.Add((left, right, status, similarity));
            }

            return result;
        }

        private List<(CSharpMember Left, CSharpMember Right, double Similarity)> ScoreAllPairs(List<CSharpMember> lefts, List<CSharpMember> rights)
        {
            var candidates = new List<(CSharpMember, CSharpMember, double)>();
            foreach (var left in lefts)
            {
                foreach (var right in rights)
                {
                    if (left.Kind != right.Kind)
                    {
                        continue;
                    }

                    var similarity = Similarity(left, right);
                    if (similarity >= _options.SimilarityThreshold)
                    {
                        candidates.Add((left, right, similarity));
                    }
                }
            }

            return candidates;
        }

        private List<(CSharpMember Left, CSharpMember Right, double Similarity)> IdenticalBodies(List<CSharpMember> lefts, List<CSharpMember> rights)
        {
            var byBody = new Dictionary<string, List<CSharpMember>>(StringComparer.Ordinal);
            foreach (var left in lefts)
            {
                var body = BodyKey(left);
                if (!byBody.TryGetValue(body, out var list))
                {
                    list = [];
                    byBody[body] = list;
                }

                list.Add(left);
            }

            var candidates = new List<(CSharpMember, CSharpMember, double)>();
            foreach (var right in rights)
            {
                if (byBody.TryGetValue(BodyKey(right), out var list))
                {
                    candidates.AddRange(list.Select(left => (left, right, 1.0)));
                }
            }

            return candidates;
        }

        /// <summary>Pairs leftovers that have the same identity in different containers, when the bodies are similar.</summary>
        private void MatchMovesAcrossContainers()
        {
            var found = true;
            while (found)
            {
                found = false;
                var byKey = _unmatchedLeft
                    .Where(member => !_leftDiffs.ContainsKey(member))
                    .ToLookup(member => member.Key, StringComparer.Ordinal);
                foreach (var right in _unmatchedRight.ToList())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (_rightDiffs.ContainsKey(right))
                    {
                        continue;
                    }

                    var best = byKey[right.Key]
                        .Where(left => !_leftDiffs.ContainsKey(left))
                        .Select(left => (Left: left, Similarity: Similarity(left, right)))
                        .Where(candidate => candidate.Similarity >= _options.SimilarityThreshold)
                        .OrderByDescending(candidate => candidate.Similarity)
                        .FirstOrDefault();
                    if (best.Left is null)
                    {
                        continue;
                    }

                    Pair(best.Left, right, MemberDiffStatus.Moved, best.Similarity);
                    found = true;
                    if (best.Left.IsContainer && right.IsContainer)
                    {
                        MatchLevel(best.Left.Children, right.Children);
                    }
                }
            }
        }

        private void AddLeftovers()
        {
            foreach (var left in _unmatchedLeft.Where(member => !_leftDiffs.ContainsKey(member)))
            {
                Register(new MemberDiff(MemberDiffStatus.Removed, left, null));
            }

            foreach (var right in _unmatchedRight.Where(member => !_rightDiffs.ContainsKey(member)))
            {
                Register(new MemberDiff(MemberDiffStatus.Added, null, right));
            }
        }

        /// <summary>Hangs each entry under the entry of its (right, else left) parent and orders siblings.</summary>
        private List<MemberDiff> BuildTree()
        {
            var roots = new List<MemberDiff>();
            foreach (var entry in _all)
            {
                MemberDiff? parent;
                if (entry.Right is not null)
                {
                    parent = entry.Right.Parent is { } rightParent ? _rightDiffs[rightParent] : null;
                }
                else
                {
                    parent = entry.Left!.Parent is { } leftParent ? _leftDiffs[leftParent] : null;
                }

                (parent?.MutableChildren ?? roots).Add(entry);
            }

            Order(roots);
            foreach (var entry in _all)
            {
                Order(entry.MutableChildren);
            }

            return roots;
        }

        /// <summary>Right-side order; removed entries go after the entry that preceded them on the left.</summary>
        private static void Order(List<MemberDiff> entries)
        {
            if (entries.Count < 2)
            {
                return;
            }

            var ordered = entries.Where(entry => entry.Right is not null).OrderBy(entry => entry.Right!.Span.Start).ToList();
            foreach (var removed in entries.Where(entry => entry.Right is null).OrderBy(entry => entry.Left!.Span.Start))
            {
                var insertAt = 0;
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Left is { } left && left.Span.Start < removed.Left!.Span.Start)
                    {
                        insertAt = i + 1;
                    }
                }

                ordered.Insert(insertAt, removed);
            }

            entries.Clear();
            entries.AddRange(ordered);
        }

        private void Pair(CSharpMember left, CSharpMember right, MemberDiffStatus status, double similarity)
        {
            var entry = new MemberDiff(status, left, right) { Similarity = similarity };

            // A rename alone is not a change of text: renamed members are compared with their names hidden.
            var hideName = status == MemberDiffStatus.Renamed;
            if (!string.Equals(CompareKey(_left, left, hideName), CompareKey(_right, right, hideName), StringComparison.Ordinal))
            {
                entry.TextChanged = true;
                entry.TextDiff = TextComparer.Compare(
                    TextDocument.Parse(_left.Fragment(left)),
                    TextDocument.Parse(_right.Fragment(right)),
                    _options.TextOptions,
                    _cancellationToken);
                if (status == MemberDiffStatus.Unchanged)
                {
                    entry.Status = MemberDiffStatus.Changed;
                }
            }

            Register(entry);
        }

        private void Register(MemberDiff entry)
        {
            if (entry.Left is not null)
            {
                _leftDiffs[entry.Left] = entry;
            }

            if (entry.Right is not null)
            {
                _rightDiffs[entry.Right] = entry;
            }

            _all.Add(entry);
        }

        /// <summary>Records unmatched members and everything inside them as candidates for moves.</summary>
        private void AddUnmatched(IEnumerable<CSharpMember> members, List<CSharpMember> target)
        {
            foreach (var member in members)
            {
                if (_unmatchedSeen.Add(member))
                {
                    target.Add(member);
                }

                AddUnmatched(member.Children, target);
            }
        }

        /// <summary>The text that decides whether a matched member changed, after the ignore options.</summary>
        private string CompareKey(SourceFile file, CSharpMember member, bool hideName)
        {
            if (hideName)
            {
                return BuildCompareKey(file, member, hideName);
            }

            if (!_compareKeys.TryGetValue(member, out var key))
            {
                key = BuildCompareKey(file, member, hideName);
                _compareKeys[member] = key;
            }

            return key;
        }

        private string BuildCompareKey(SourceFile file, CSharpMember member, bool hideName)
        {
            var ignore = _options.Ignore & ~CSharpIgnoreOptions.UsingOrder;
            if ((ignore & CSharpIgnoreOptions.Formatting) != 0)
            {
                return TokenKey(member, ignore, hideName);
            }

            var fragment = file.Fragment(member);
            if (hideName && member.Identifier.Span.Length > 0 && member.Identifier.SpanStart >= file.FragmentStart(member))
            {
                var offset = member.Identifier.SpanStart - file.FragmentStart(member);
                fragment = fragment.Substring(0, offset) + HiddenName + fragment.Substring(offset + member.Identifier.Span.Length);
            }

            if (ignore != CSharpIgnoreOptions.None)
            {
                return CSharpNormalizer.Normalize(fragment, ignore, _cancellationToken).Text;
            }

            return fragment.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Tokens and kept comments or directives of the compared span, separated by single spaces.</summary>
        private static string TokenKey(CSharpMember member, CSharpIgnoreOptions ignore, bool hideName)
        {
            var span = TextSpan.FromBounds(member.CompareSpan.Start, member.CompareSpan.End);
            var builder = new StringBuilder();
            foreach (var token in member.Node.DescendantTokens(span))
            {
                if (token.SpanStart < span.Start || token.Span.End > span.End)
                {
                    continue;
                }

                AppendTriviaKey(builder, token.LeadingTrivia, span, ignore);
                builder.Append(hideName && token == member.Identifier ? HiddenName : token.Text).Append(' ');
                AppendTriviaKey(builder, token.TrailingTrivia, span, ignore);
            }

            return builder.ToString();
        }

        private static void AppendTriviaKey(StringBuilder builder, SyntaxTriviaList trivia, TextSpan span, CSharpIgnoreOptions ignore)
        {
            foreach (var item in trivia)
            {
                if (!span.Contains(item.Span) || !KeepsTrivia(item.Kind(), ignore))
                {
                    continue;
                }

                var words = item.ToFullString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                builder.Append(string.Join(" ", words)).Append(' ');
            }
        }

        private static bool KeepsTrivia(SyntaxKind kind, CSharpIgnoreOptions ignore)
        {
            switch (kind)
            {
                case SyntaxKind.WhitespaceTrivia:
                case SyntaxKind.EndOfLineTrivia:
                    return false;

                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                    return (ignore & CSharpIgnoreOptions.Comments) == 0;

                case SyntaxKind.RegionDirectiveTrivia:
                case SyntaxKind.EndRegionDirectiveTrivia:
                    return (ignore & CSharpIgnoreOptions.Regions) == 0;

                default:
                    return true;
            }
        }

        /// <summary>Token similarity of two members, ignoring their names: 1 - changed tokens / all tokens.</summary>
        private double Similarity(CSharpMember left, CSharpMember right)
        {
            var leftTokens = Tokens(left);
            var rightTokens = Tokens(right);
            var total = leftTokens.Length + rightTokens.Length;
            if (total == 0)
            {
                return 1;
            }

            // The similarity can never exceed 2 * shorter / total; skip the diff when that is already too low.
            var upperBound = 2.0 * Math.Min(leftTokens.Length, rightTokens.Length) / total;
            if (upperBound < _options.SimilarityThreshold)
            {
                return upperBound;
            }

            var hunks = TextComparer.ComputeHunks(leftTokens, rightTokens, DiffAlgorithmKind.Histogram, _cancellationToken);
            var changed = hunks.Sum(hunk => hunk.LeftCount + hunk.RightCount);
            return 1.0 - (double)changed / total;
        }

        private int[] Tokens(CSharpMember member)
        {
            if (_tokens.TryGetValue(member, out var tokens))
            {
                return tokens;
            }

            var ids = new List<int>();
            foreach (var token in member.Node.DescendantTokens())
            {
                if (token == member.Identifier)
                {
                    continue;
                }

                if (!_tokenIds.TryGetValue(token.Text, out var id))
                {
                    id = _tokenIds.Count;
                    _tokenIds[token.Text] = id;
                }

                ids.Add(id);
            }

            tokens = ids.ToArray();
            _tokens[member] = tokens;
            return tokens;
        }

        private string BodyKey(CSharpMember member) =>
            member.Kind + ":" + string.Join(",", Tokens(member));

        /// <summary>Marks the members of a longest strictly increasing subsequence, in O(n log n).</summary>
        private static bool[] LongestIncreasingRun(int[] values)
        {
            var inRun = new bool[values.Length];
            var tails = new List<int>();
            var previous = new int[values.Length];
            foreach (var i in Enumerable.Range(0, values.Length))
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

            for (var i = tails.Count > 0 ? tails[tails.Count - 1] : -1; i >= 0; i = previous[i])
            {
                inRun[i] = true;
            }

            return inRun;
        }
    }
}
