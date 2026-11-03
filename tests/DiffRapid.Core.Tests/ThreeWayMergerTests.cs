using DiffRapid.Core.Merge;
using static DiffRapid.Core.Tests.TextComparerTests;

namespace DiffRapid.Core.Tests;

public class ThreeWayMergerTests
{
    [Fact]
    public void Non_overlapping_changes_merge_cleanly()
    {
        var @base = Doc("a", "b", "c", "d", "e");
        var left = Doc("A", "b", "c", "d", "e");
        var right = Doc("a", "b", "c", "d", "E", "f");

        var result = ThreeWayMerger.Merge(@base, left, right);

        Assert.False(result.HasConflicts);
        Assert.Equal("A\nb\nc\nd\nE\nf\n", result.ToText());
    }

    [Fact]
    public void Identical_changes_on_both_sides_are_not_conflicts()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "b"), Doc("a", "B"), Doc("a", "B"));

        Assert.False(result.HasConflicts);
        Assert.Contains(result.Chunks, c => c.Kind == MergeChunkKind.BothSame);
        Assert.Equal("a\nB\n", result.ToText());
    }

    [Fact]
    public void Overlapping_different_changes_conflict_with_git_markers()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "b", "c"), Doc("a", "L", "c"), Doc("a", "R", "c"));

        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "a\n<<<<<<< mine\nL\n=======\nR\n>>>>>>> theirs\nc\n",
            result.ToText(new ConflictLabels("mine", "base", "theirs")));
        Assert.Equal(
            "a\n<<<<<<< left\nL\n||||||| base\nb\n=======\nR\n>>>>>>> right\nc\n",
            result.ToText(includeBase: true));
    }

    [Fact]
    public void Insertions_at_the_same_place_conflict()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "z"), Doc("a", "x", "z"), Doc("a", "y", "z"));

        Assert.Equal(1, result.ConflictCount);
    }

    [Fact]
    public void Deletion_on_one_side_is_applied()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "b", "c"), Doc("a", "c"), Doc("a", "b", "c", "d"));

        Assert.False(result.HasConflicts);
        Assert.Equal("a\nc\nd\n", result.ToText());
    }

    [Fact]
    public void Empty_base_with_identical_sides_merges()
    {
        var result = ThreeWayMerger.Merge(Doc(), Doc("x"), Doc("x"));

        Assert.False(result.HasConflicts);
        Assert.Equal("x\n", result.ToText());
    }

    [Fact]
    public void Random_merges_without_conflicts_agree_with_DiffPlex_oracle()
    {
        var random = new Random(4242);
        var checkedCases = 0;
        for (var iteration = 0; iteration < 3000 && checkedCases < 300; iteration++)
        {
            var baseLines = Enumerable.Range(0, random.Next(5, 25)).Select(i => "line" + i).ToList();
            var left = EditFarApart(baseLines, random, 0);
            var right = EditFarApart(baseLines, random, 1);

            var result = ThreeWayMerger.Merge(Doc([.. baseLines]), Doc([.. left]), Doc([.. right]));
            var oracle = new DiffPlex.ThreeWayDiffer().CreateMerge(
                string.Join("\n", baseLines), string.Join("\n", left), string.Join("\n", right),
                ignoreWhiteSpace: false, ignoreCase: false, new DiffPlex.Chunkers.LineChunker());

            if (result.HasConflicts || oracle.IsSuccessful is false)
            {
                continue;
            }

            checkedCases++;
            Assert.Equal(string.Join("\n", oracle.MergedPieces), result.ToText().TrimEnd('\n'));
        }

        Assert.True(checkedCases > 50, $"Only {checkedCases} clean merges were checked.");
    }

    // Edits only even (side 0) or odd (side 1) thirds of the file so most merges are clean.
    private static List<string> EditFarApart(List<string> source, Random random, int side)
    {
        var result = new List<string>(source);
        var third = Math.Max(1, source.Count / 3);
        var zoneStart = side == 0 ? 0 : 2 * third;
        var zoneEnd = Math.Min(source.Count, zoneStart + third);
        if (zoneEnd <= zoneStart)
        {
            return result;
        }

        var index = random.Next(zoneStart, zoneEnd);
        switch (random.Next(3))
        {
            case 0:
                result[index] = $"edited-{side}-{index}";
                break;
            case 1:
                result.RemoveAt(index);
                break;
            default:
                result.Insert(index, $"inserted-{side}-{index}");
                break;
        }

        return result;
    }
}
