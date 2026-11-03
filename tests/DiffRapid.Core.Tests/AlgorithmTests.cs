using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Tests;

public class AlgorithmTests
{
    public static TheoryData<DiffAlgorithmKind> AllAlgorithms => new()
    {
        DiffAlgorithmKind.Default,
        DiffAlgorithmKind.Minimal,
        DiffAlgorithmKind.Patience,
        DiffAlgorithmKind.Histogram,
        DiffAlgorithmKind.None,
    };

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Hunks_describe_a_valid_edit_script_for_random_input(DiffAlgorithmKind kind)
    {
        var random = new Random(12345);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var a = RandomSequence(random);
            var b = random.Next(4) == 0 ? Mutate(a, random) : RandomSequence(random);

            var hunks = TextComparer.ComputeHunks(a, b, kind, CancellationToken.None);

            AssertValidEditScript(a, b, hunks);
        }
    }

    [Theory]
    [InlineData(DiffAlgorithmKind.Default)]
    [InlineData(DiffAlgorithmKind.Minimal)]
    public void Myers_produces_a_minimal_edit_script(DiffAlgorithmKind kind)
    {
        var random = new Random(777);
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var a = RandomSequence(random);
            var b = Mutate(a, random);

            var hunks = TextComparer.ComputeHunks(a, b, kind, CancellationToken.None);
            var edits = hunks.Sum(h => h.LeftCount + h.RightCount);

            Assert.Equal(a.Length + b.Length - (2 * LcsLength(a, b)), edits);
        }
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Identical_sequences_have_no_hunks(DiffAlgorithmKind kind)
    {
        int[] a = [1, 2, 3, 2, 1];
        Assert.Empty(TextComparer.ComputeHunks(a, (int[])a.Clone(), kind, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Empty_sides_are_pure_insertions_or_deletions(DiffAlgorithmKind kind)
    {
        var inserted = TextComparer.ComputeHunks([], [1, 2], kind, CancellationToken.None);
        var deleted = TextComparer.ComputeHunks([1, 2], [], kind, CancellationToken.None);

        Assert.Equal([new DiffHunk(0, 0, 0, 2)], inserted);
        Assert.Equal([new DiffHunk(0, 2, 0, 0)], deleted);
    }

    [Fact]
    public void Histogram_prefers_rare_lines_as_anchors()
    {
        // Moving a function: the brace lines are common, the signature lines are unique.
        int[] a = [10, 1, 2, 3, 99, 20, 1, 2, 3, 99];
        int[] b = [20, 1, 2, 3, 99, 10, 1, 2, 3, 99];

        var hunks = TextComparer.ComputeHunks(a, b, DiffAlgorithmKind.Histogram, CancellationToken.None);

        AssertValidEditScript(a, b, hunks);
    }

    [Fact]
    public void Cancellation_is_observed()
    {
        var a = Enumerable.Range(0, 20000).ToArray();
        var b = Enumerable.Range(0, 20000).Select(x => x + 100000).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => TextComparer.ComputeHunks(a, b, DiffAlgorithmKind.Default, cts.Token));
    }

    internal static void AssertValidEditScript(int[] a, int[] b, IReadOnlyList<DiffHunk> hunks)
    {
        int i = 0, j = 0;
        foreach (var hunk in hunks)
        {
            Assert.True(hunk.LeftStart >= i && hunk.RightStart >= j, "Hunks must be ordered and non-overlapping.");
            Assert.Equal(hunk.LeftStart - i, hunk.RightStart - j);
            while (i < hunk.LeftStart)
            {
                Assert.Equal(a[i++], b[j++]);
            }

            Assert.True(hunk.LeftCount + hunk.RightCount > 0);
            i = hunk.LeftEnd;
            j = hunk.RightEnd;
        }

        Assert.Equal(a.Length - i, b.Length - j);
        while (i < a.Length)
        {
            Assert.Equal(a[i++], b[j++]);
        }
    }

    private static int[] RandomSequence(Random random)
    {
        var length = random.Next(0, 40);
        var alphabet = random.Next(1, 8);
        return Enumerable.Range(0, length).Select(_ => random.Next(alphabet)).ToArray();
    }

    private static int[] Mutate(int[] source, Random random)
    {
        var list = source.ToList();
        var edits = random.Next(0, 6);
        for (var e = 0; e < edits; e++)
        {
            var op = random.Next(3);
            if (op == 0 || list.Count == 0)
            {
                list.Insert(random.Next(list.Count + 1), random.Next(10));
            }
            else if (op == 1)
            {
                list.RemoveAt(random.Next(list.Count));
            }
            else
            {
                list[random.Next(list.Count)] = random.Next(10);
            }
        }

        return list.ToArray();
    }

    private static int LcsLength(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        return dp[0, 0];
    }
}
