using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;
using static DiffRapid.Core.Tests.TextComparerTests;

namespace DiffRapid.Core.Tests;

public class ConflictResolverTests
{
    private static MergeSession Session(TextDocument @base, TextDocument left, TextDocument right) =>
        new(ThreeWayMerger.Merge(@base, left, right));

    private static int ConflictIndex(MergeSession session) => session.NextConflict(-1);

    [Fact]
    public void Identical_sides_take_the_left_lines()
    {
        // The merger already treats identical changes as BothSame, so build the conflict directly.
        var chunk = new MergeChunk(MergeChunkKind.Conflict, 0, 1, 0, 1, 0, 1);
        var session = new MergeSession(new MergeResult(Doc("a"), Doc("b"), Doc("b"), new[] { chunk }));
        var resolution = Assert.Single(ConflictResolver.ResolveSimple(session));
        Assert.Equal(new SimpleResolution(0, SimpleResolutionKind.SameChange, MergeResolution.Left), resolution);
        Assert.Equal("b\n", session.ToText());
    }

    [Fact]
    public void Whitespace_only_side_yields_to_the_other_change()
    {
        var session = Session(Doc("a", "x = 1;", "c"), Doc("a", "x  =  1;", "c"), Doc("a", "x = 2;", "c"));
        var conflict = ConflictIndex(session);
        Assert.Equal(new SimpleResolution(conflict, SimpleResolutionKind.LeftWhitespaceOnly, MergeResolution.Right),
            Assert.Single(ConflictResolver.ResolveSimple(session)));
        Assert.Equal("a\nx = 2;\nc\n", session.ToText());

        var mirror = Session(Doc("a", "x = 1;", "c"), Doc("a", "x = 2;", "c"), Doc("a", "\tx = 1;", "c"));
        Assert.Equal(SimpleResolutionKind.RightWhitespaceOnly, Assert.Single(ConflictResolver.ResolveSimple(mirror)).Kind);
        Assert.Equal("a\nx = 2;\nc\n", mirror.ToText());
    }

    [Fact]
    public void Whitespace_changes_on_both_sides_stay_in_conflict()
    {
        var session = Session(Doc("x = 1;"), Doc("x  = 1;"), Doc("x =  1;"));
        Assert.Empty(ConflictResolver.ResolveSimple(session));
        Assert.Equal(1, session.RemainingConflictCount);
    }

    [Fact]
    public void Changes_to_different_lines_are_combined()
    {
        var session = Session(Doc("a", "b", "c"), Doc("A", "b", "c"), Doc("a", "B", "c"));
        Assert.Equal(1, session.ConflictCount);
        var resolution = Assert.Single(ConflictResolver.ResolveSimple(session));
        Assert.Equal(SimpleResolutionKind.DifferentLines, resolution.Kind);
        Assert.Equal(MergeResolution.Custom, session.Resolution(resolution.Chunk));
        Assert.Equal("A\nB\nc\n", session.ToText());
        Assert.Equal(0, session.RemainingConflictCount);
        Assert.Equal(1, session.ResolvedConflictCount);
    }

    [Fact]
    public void Identical_change_inside_a_block_is_taken_once()
    {
        var text = "<<<<<<< ours\nA\nb\nC\nd\ne\n||||||| base\na\nb\nc\nd\ne\n=======\na\nb\nC\nd\nE\n>>>>>>> theirs\n";
        var resolution = ConflictResolver.ResolveConflictText(text);
        Assert.Equal(SimpleResolutionKind.DifferentLines, Assert.Single(resolution.Resolutions).Kind);
        Assert.Equal("A\nb\nC\nd\nE\n", resolution.Text);

        // The right side rewrote b and c together, which overlaps the left's change to c differently.
        var session = Session(Doc("a", "b", "c", "d"), Doc("A", "b", "C", "d"), Doc("a", "B", "C", "d"));
        Assert.Empty(ConflictResolver.ResolveSimple(session));
    }

    [Fact]
    public void Combined_lines_keep_their_own_line_endings()
    {
        var session = Session(
            TextDocument.Parse("a\r\nb\r\nc"),
            TextDocument.Parse("A\r\nb\r\nc"),
            TextDocument.Parse("a\r\nB\r\nc"));
        Assert.Single(ConflictResolver.ResolveSimple(session));
        Assert.Equal("A\r\nB\r\nc", session.ToText());
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, new[] { "a", "X", "b" }, new[] { "a", "B" })]
    [InlineData(new[] { "a", "b", "c" }, new[] { "a", "L", "c" }, new[] { "a", "R", "c" })]
    [InlineData(new[] { "a", "b" }, new[] { "X", "a", "b" }, new[] { "Y", "a", "b" })]
    public void Overlapping_or_adjacent_insertions_stay_in_conflict(string[] @base, string[] left, string[] right)
    {
        var session = Session(Doc(@base), Doc(left), Doc(right));
        Assert.True(session.ConflictCount > 0);
        Assert.Empty(ConflictResolver.ResolveSimple(session));
        Assert.Equal(session.Result.ToText(), session.ToText());
    }

    [Fact]
    public void Existing_choices_are_not_overridden()
    {
        var session = Session(Doc("a", "b", "c"), Doc("A", "b", "c"), Doc("a", "B", "c"));
        var conflict = ConflictIndex(session);
        session.Resolve(conflict, MergeResolution.Base);
        Assert.Empty(ConflictResolver.ResolveSimple(session));
        Assert.Equal(MergeResolution.Base, session.Resolution(conflict));
    }

    [Fact]
    public void Resolve_simple_honours_cancellation()
    {
        var session = Session(Doc("a", "b", "c"), Doc("A", "b", "c"), Doc("a", "B", "c"));
        Assert.Throws<OperationCanceledException>(() => ConflictResolver.ResolveSimple(session, new CancellationToken(true)));
        Assert.Throws<ArgumentNullException>(() => ConflictResolver.ResolveSimple(null!));
    }

    [Fact]
    public void Accept_both_reset_and_counters()
    {
        var session = Session(Doc("a", "b", "c"), Doc("a", "L", "c"), Doc("a", "R", "c"));
        var conflict = ConflictIndex(session);
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.Equal((1, 0, 1), (session.ConflictCount, session.ResolvedConflictCount, session.RemainingConflictCount));

        session.AcceptBoth(conflict, leftFirst: false);
        Assert.Equal("a\nR\nL\nc\n", session.ToText());
        session.AcceptBoth(conflict, leftFirst: true);
        Assert.Equal("a\nL\nR\nc\n", session.ToText());
        Assert.Equal((1, 0), (session.ResolvedConflictCount, session.RemainingConflictCount));

        session.SetCustomText(conflict, new[] { "mine" });
        session.ResetConflict(conflict);
        Assert.Equal(MergeResolution.Unresolved, session.Resolution(conflict));
        Assert.Throws<InvalidOperationException>(() => session.Resolve(conflict, MergeResolution.Custom));
        Assert.Equal(session.Result.ToText(), session.ToText());
        Assert.Equal(4, changes);
        session.ResetConflict(conflict);
        Assert.Equal(4, changes);

        session.Resolve(0, MergeResolution.Right);
        session.ResetConflict(0);
        Assert.Equal(MergeResolution.Base, session.Resolution(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ResetConflict(session.Result.Chunks.Count));
    }

    private const string ConflictText =
        "head\n" +
        "<<<<<<< ours\nA\nb\n||||||| base\na\nb\n=======\na\nB\n>>>>>>> theirs\n" +
        "mid\n" +
        "<<<<<<< ours\nx1\n=======\nx2\n>>>>>>> theirs\n" +
        "<<<<<<< ours\nsame\n=======\nsame\n>>>>>>> theirs\n" +
        "tail";

    [Fact]
    public void Conflict_text_resolves_simple_blocks_and_keeps_the_rest()
    {
        var resolution = ConflictResolver.ResolveConflictText(ConflictText);
        Assert.Equal(3, resolution.ConflictCount);
        Assert.Equal(2, resolution.ResolvedCount);
        Assert.Equal(1, resolution.RemainingCount);
        Assert.Equal(new[] { 0, 2 }, resolution.Resolutions.Select(r => r.Chunk));
        Assert.Equal(new[] { SimpleResolutionKind.DifferentLines, SimpleResolutionKind.SameChange }, resolution.Resolutions.Select(r => r.Kind));
        Assert.Equal("head\nA\nB\nmid\n<<<<<<< ours\nx1\n=======\nx2\n>>>>>>> theirs\nsame\ntail", resolution.Text);
    }

    [Fact]
    public void Conflict_text_without_conflicts_or_malformed()
    {
        var plain = ConflictResolver.ResolveConflictText("a\r\nb");
        Assert.Equal((0, "a\r\nb"), (plain.ConflictCount, plain.Text));
        Assert.Throws<FormatException>(() => ConflictResolver.ResolveConflictText("<<<<<<< x\na\n"));
    }

    [Fact]
    public void Cli_resolve_writes_output_and_sets_exit_code()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            var input = Path.Combine(directory, "file.txt");
            var output = Path.Combine(directory, "out.txt");
            File.WriteAllText(input, ConflictText, encoding);
            Assert.Equal(1, Program.Main(["resolve", input, "-o", output]));
            var expected = ConflictResolver.ResolveConflictText(ConflictText).Text;
            Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), File.ReadAllBytes(output));

            File.WriteAllText(input, "x\n<<<<<<< a\nsame\n=======\nsame\n>>>>>>> b\n");
            Assert.Equal(0, Program.Main(["resolve", input, "--output", output]));
            Assert.Equal("x\nsame\n", File.ReadAllText(output));

            File.WriteAllText(input, "=======\n");
            Assert.Equal(2, Program.Main(["resolve", input, "-o", output]));
            Assert.Equal(2, Program.Main(["resolve"]));
            Assert.Equal(2, Program.Main(["resolve", Path.Combine(directory, "missing")]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
