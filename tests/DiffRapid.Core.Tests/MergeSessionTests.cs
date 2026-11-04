using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;
using static DiffRapid.Core.Tests.TextComparerTests;

namespace DiffRapid.Core.Tests;

public class MergeSessionTests
{
    private static MergeResult Conflict() => ThreeWayMerger.Merge(Doc("a", "b", "c"), Doc("a", "L", "c"), Doc("a", "R", "c"));

    [Fact]
    public void Initial_choices_follow_chunk_kind()
    {
        foreach (var result in new[]
        {
            ThreeWayMerger.Merge(Doc("a"), Doc("a"), Doc("a")),
            ThreeWayMerger.Merge(Doc("a"), Doc("L"), Doc("a")),
            ThreeWayMerger.Merge(Doc("a"), Doc("a"), Doc("R")),
            ThreeWayMerger.Merge(Doc("a"), Doc("L"), Doc("L")),
            Conflict(),
        })
        {
            var session = new MergeSession(result);
            for (var i = 0; i < result.Chunks.Count; i++)
            {
                var expected = result.Chunks[i].Kind switch
                {
                    MergeChunkKind.Unchanged => MergeResolution.Base,
                    MergeChunkKind.LeftOnly or MergeChunkKind.BothSame => MergeResolution.Left,
                    MergeChunkKind.RightOnly => MergeResolution.Right,
                    _ => MergeResolution.Unresolved,
                };
                Assert.Equal(expected, session.Resolution(i));
            }

            Assert.Equal(result.ConflictCount, session.UnresolvedCount);
            Assert.Equal(result.ToText(), session.ToText());
        }
    }

    [Theory]
    [InlineData(MergeResolution.Left, "L\n")]
    [InlineData(MergeResolution.Right, "R\n")]
    [InlineData(MergeResolution.Base, "b\n")]
    [InlineData(MergeResolution.LeftThenRight, "L\nR\n")]
    [InlineData(MergeResolution.RightThenLeft, "R\nL\n")]
    public void Choices_render_selected_sources(MergeResolution choice, string text)
    {
        var session = new MergeSession(Conflict());
        session.Resolve(1, choice);
        Assert.Equal("a\n" + text + "c\n", session.ToText());
        Assert.Equal(0, session.UnresolvedCount);
        session.Resolve(1, MergeResolution.Unresolved);
        Assert.Equal(Conflict().ToText(), session.ToText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unresolved_output_matches_original(bool includeBase)
    {
        foreach (var ending in new[] { "\n", "\r\n", "\r", "" })
        {
            var result = ThreeWayMerger.Merge(TextDocument.Parse("b" + ending), TextDocument.Parse("L" + ending), TextDocument.Parse("R" + ending));
            var labels = new ConflictLabels("mine", "ancestor", "theirs");
            Assert.Equal(result.ToText(labels, includeBase), new MergeSession(result).ToText(labels, includeBase));
        }
    }

    [Fact]
    public void Custom_text_is_copied_and_changes_are_reported()
    {
        var session = new MergeSession(Conflict());
        var changes = 0;
        session.Changed += (sender, _) => { Assert.Same(session, sender); changes++; };
        var lines = new[] { "custom", "text" };
        session.SetCustomText(1, lines);
        lines[0] = "mutated";
        Assert.Equal(MergeResolution.Custom, session.Resolution(1));
        Assert.Equal("a\ncustom\ntext\nc\n", session.ToText());
        session.ResolveAllConflicts(MergeResolution.Right);
        session.Resolve(1, MergeResolution.Custom);
        Assert.Equal(3, changes);
        session.SetCustomText(1, Array.Empty<string>());
        Assert.Equal("a\nc\n", session.ToText());
    }

    [Fact]
    public void Navigation_includes_resolved_conflicts_and_boundaries()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "b", "c"), Doc("L", "b", "LL"), Doc("R", "b", "RR"));
        var session = new MergeSession(result);
        session.ResolveAllConflicts(MergeResolution.Left);
        Assert.Equal(0, session.UnresolvedCount);
        Assert.Equal(0, session.NextConflict(-1));
        Assert.Equal(2, session.NextConflict(0));
        Assert.Equal(-1, session.NextConflict(2));
        Assert.Equal(-1, session.NextConflict(3));
        Assert.Equal(2, session.PreviousConflict(3));
        Assert.Equal(0, session.PreviousConflict(2));
        Assert.Equal(-1, session.PreviousConflict(0));
        Assert.Equal(-1, session.PreviousConflict(-1));
        var empty = new MergeSession(ThreeWayMerger.Merge(Doc(), Doc(), Doc()));
        Assert.Equal(-1, empty.NextConflict(-1));
        Assert.Equal(-1, empty.PreviousConflict(0));
    }

    [Fact]
    public void Ranges_cover_rendered_lines_after_each_resolution()
    {
        var session = new MergeSession(Conflict());
        foreach (var resolution in Enum.GetValues<MergeResolution>())
        {
            session.SetCustomText(1, new[] { "x", "y" });
            session.Resolve(1, resolution);
            var lines = TextDocument.Parse(session.ToText());
            var position = 0;
            for (var i = 0; i < 3; i++)
            {
                var range = session.OutputLineRange(i);
                Assert.Equal(position, range.Start);
                position += range.Count;
            }

            Assert.Equal(lines.LineCount, position);
        }
    }

    [Fact]
    public void Ranges_follow_diff3_markers_and_empty_chunks()
    {
        var session = new MergeSession(Conflict());
        var labels = new ConflictLabels("mine", "ancestor", "theirs");
        Assert.Equal((1, 7), session.OutputLineRange(1, labels, includeBase: true));
        Assert.Equal((8, 1), session.OutputLineRange(2, labels, includeBase: true));
        Assert.Equal(9, TextDocument.Parse(session.ToText(labels, includeBase: true)).LineCount);
        session.SetCustomText(1, Array.Empty<string>());
        Assert.Equal((1, 0), session.OutputLineRange(1));
        Assert.Equal((1, 1), session.OutputLineRange(2));
    }
    [Fact]
    public void Concatenation_keeps_missing_and_mixed_terminators()
    {
        var result = ThreeWayMerger.Merge(TextDocument.Parse("b"), TextDocument.Parse("L"), TextDocument.Parse("R\r\n"));
        var session = new MergeSession(result);
        session.Resolve(0, MergeResolution.LeftThenRight);
        Assert.Equal("LR\r\n", session.ToText());
        Assert.Equal((0, 1), session.OutputLineRange(0));
        session.Resolve(0, MergeResolution.RightThenLeft);
        Assert.Equal("R\r\nL", session.ToText());
        Assert.Equal((0, 2), session.OutputLineRange(0));
    }

    [Fact]
    public void Crlf_sources_and_custom_text_keep_crlf()
    {
        var session = new MergeSession(ThreeWayMerger.Merge(TextDocument.Parse("b\r\n"), TextDocument.Parse("L\r\n"), TextDocument.Parse("R\r\n")));
        session.Resolve(0, MergeResolution.LeftThenRight);
        Assert.Equal("L\r\nR\r\n", session.ToText());
        session.SetCustomText(0, new[] { "custom" });
        Assert.Equal("custom\r\n", session.ToText());
    }

    [Fact]
    public void Alignment_pairs_lines_and_pads_shorter_sides()
    {
        var result = ThreeWayMerger.Merge(Doc("a", "b", "c"), Doc("a", "L", "LL", "c"), Doc("a", "c"));
        var rows = MergeAlignment.Build(result);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new MergeRow(0, MergeChunkKind.Unchanged, 0, 0, 0), rows[0]);
        Assert.Equal(new MergeRow(1, MergeChunkKind.Conflict, 1, 1, null), rows[1]);
        Assert.Equal(new MergeRow(1, MergeChunkKind.Conflict, null, 2, null), rows[2]);
        Assert.Equal(new MergeRow(2, MergeChunkKind.Unchanged, 2, 3, 1), rows[3]);
        Assert.Empty(MergeAlignment.Build(ThreeWayMerger.Merge(Doc(), Doc(), Doc())));
    }

    [Fact]
    public void Invalid_arguments_are_rejected_without_partial_bulk_changes()
    {
        var session = new MergeSession(Conflict());
        foreach (var index in new[] { -1, 3 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Resolution(index));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Resolve(index, MergeResolution.Left));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.SetCustomText(index, Array.Empty<string>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.OutputLineRange(index));
        }

        Assert.Throws<InvalidOperationException>(() => session.Resolve(1, MergeResolution.Custom));
        Assert.Throws<InvalidOperationException>(() => session.ResolveAllConflicts(MergeResolution.Custom));
        Assert.Equal(1, session.UnresolvedCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Resolve(1, (MergeResolution)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ResolveAllConflicts((MergeResolution)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.NextConflict(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.PreviousConflict(4));
        Assert.Throws<ArgumentNullException>(() => new MergeSession(null!));
        Assert.Throws<ArgumentNullException>(() => MergeAlignment.Build(null!));
        Assert.Throws<ArgumentNullException>(() => session.SetCustomText(1, null!));
    }
}
