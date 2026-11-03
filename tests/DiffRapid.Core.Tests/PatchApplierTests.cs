using DiffRapid.Core.Patching;

namespace DiffRapid.Core.Tests;

public sealed class PatchApplierTests
{
    private static FilePatch Change => Patch.Parse("--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n top\n-old\n+new\n end\n").Files[0];

    [Fact]
    public void PureInsertionsAreNotDuplicated()
    {
        var file = Patch.Parse("--- a/x\n+++ b/x\n@@ -0,0 +1 @@\n+added\n").Files[0];
        var result = PatchApplier.Apply("added\n", file);
        Assert.Equal("added\n", result.Text);
        Assert.Equal(HunkStatus.AlreadyApplied, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public void PositiveOffsetAndOriginalTerminatorsArePreserved()
    {
        var result = PatchApplier.Apply("extra\r\ntop\r\nold\r\nend\r\n", Change);
        Assert.Equal("extra\r\ntop\r\nnew\r\nend\r\n", result.Text);
        Assert.Equal(new HunkOutcome(HunkStatus.AppliedWithOffset, 1), Assert.Single(result.Outcomes));
    }

    [Fact]
    public void NegativeOffsetIsReported()
    {
        var file = Change with { Hunks = [Change.Hunks[0] with { OldStart = 3, NewStart = 3 }] };
        var result = PatchApplier.Apply("top\nold\nend\n", file);
        Assert.Equal("top\nnew\nend\n", result.Text);
        Assert.Equal(new HunkOutcome(HunkStatus.AppliedWithOffset, -2), Assert.Single(result.Outcomes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FuzzIgnoresOnlyRequestedEdgeContext(int fuzz)
    {
        var file = Patch.Parse("--- a/x\n+++ b/x\n@@ -1,5 +1,5 @@\n first\n second\n-old\n+new\n fourth\n fifth\n").Files[0];
        var original = fuzz == 1 ? "changed\nsecond\nold\nfourth\nchanged\n" : "changed\nalso changed\nold\nalso changed\nchanged\n";
        var result = PatchApplier.Apply(original, file, new PatchApplyOptions { Fuzz = fuzz });
        Assert.True(result.Succeeded);
        Assert.Equal(original.Replace("old\n", "new\n"), result.Text);
        Assert.Equal(new HunkOutcome(HunkStatus.AppliedWithFuzz), Assert.Single(result.Outcomes));
        var insufficient = PatchApplier.Apply(original, file, new PatchApplyOptions { Fuzz = fuzz - 1 });
        Assert.False(insufficient.Succeeded);
        Assert.Equal(original, insufficient.Text);
        Assert.False(PatchApplier.Apply(original.Replace("old\n", "wrong\n"), file).Succeeded);
    }

    [Fact]
    public void FailureLeavesOtherHunksAppliedAndContinues()
    {
        var file = Patch.Parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+A\n@@ -3 +3 @@\n-c\n+C\n@@ -5 +5 @@\n-e\n+E\n").Files[0];
        var result = PatchApplier.Apply("a\nb\nwrong\nd\ne\n", file, new PatchApplyOptions { Fuzz = 0 });
        Assert.False(result.Succeeded);
        Assert.Equal("A\nb\nwrong\nd\nE\n", result.Text);
        Assert.Equal(new[] { HunkStatus.Applied, HunkStatus.Failed, HunkStatus.Applied }, result.Outcomes.Select(o => o.Status));
    }

    [Fact]
    public void AlreadyAppliedChangePreservesText()
    {
        var result = PatchApplier.Apply("top\nnew\nend\n", Change);
        Assert.True(result.Succeeded);
        Assert.Equal("top\nnew\nend\n", result.Text);
        Assert.Equal(HunkStatus.AlreadyApplied, Assert.Single(result.Outcomes).Status);
    }

    [Fact]
    public void WhitespaceMatchingIsOptional()
    {
        var file = Patch.Parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a b\n+c d\n").Files[0];
        Assert.False(PatchApplier.Apply(" a\t b \n", file).Succeeded);
        var result = PatchApplier.Apply(" a\t b \n", file, new PatchApplyOptions { IgnoreWhitespace = true });
        Assert.True(result.Succeeded);
        Assert.Equal("c d\n", result.Text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidFuzzIsRejected(int fuzz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchApplier.Apply("", Change, new PatchApplyOptions { Fuzz = fuzz }));
    }
}
