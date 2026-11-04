using DiffRapid.Core.Comparison;
using DiffRapid.Core.Output;
using DiffRapid.Core.Patching;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public sealed class PatchTests
{
    public static IEnumerable<object[]> TextPairs()
    {
        foreach (var ending in new[] { "\n", "\r\n" })
        {
            var lines = Enumerable.Range(1, 7).Select(index => "line " + index + ending).ToArray();
            var original = string.Concat(lines);
            foreach (var position in new[] { 0, 3, 6 })
            {
                var inserted = lines.ToList();
                inserted.Insert(position == 6 ? lines.Length : position, "inserted" + ending);
                var deleted = lines.ToList();
                deleted.RemoveAt(position);
                var changed = lines.ToArray();
                changed[position] = "changed" + ending;
                foreach (var right in new[] { string.Concat(inserted), string.Concat(deleted), string.Concat(changed) })
                {
                    foreach (var context in new[] { 0, 1, 3 })
                    {
                        yield return [original, right, context];
                    }
                }
            }
        }

        // Retained mixed terminators remain unchanged; inserted lines use the dominant terminator.
        foreach (var pair in new (string Left, string Right)[]
        {
            ("", "added\n"), ("removed\n", ""), ("", "added"), ("removed", ""),
            ("old", "new\n"), ("old\n", "new"), ("old", "new"),
            ("top\r\nold\nend\n", "top\r\nnew\nend\n"),
            ("top\nold\nend\r\n", "top\nnew\nend\r\n"),
        })
        {
            foreach (var context in new[] { 0, 1, 3 })
            {
                yield return [pair.Left, pair.Right, context];
            }
        }
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void TextPairsRoundTripInBothDirections(string left, string right, int context)
    {
        var text = UnifiedDiffWriter.Write(TextComparer.Compare(TextDocument.Parse(left), TextDocument.Parse(right)), "a/file", "b/file", context);
        var file = Assert.Single(Patch.Parse(text).Files);
        var forward = PatchApplier.Apply(left, file);
        Assert.True(forward.Succeeded);
        Assert.Equal(right, forward.Text);
        var reverse = PatchApplier.Apply(right, file, new PatchApplyOptions { Reverse = true });
        Assert.True(reverse.Succeeded);
        Assert.Equal(left, reverse.Text);
    }

    [Theory]
    [InlineData("new file mode 100644", null, "x")]
    [InlineData("deleted file mode 100644", "x", null)]
    public void GitEmptyFilesAreRecognized(string mode, string? oldPath, string? newPath)
    {
        var file = Assert.Single(Patch.Parse($"diff --git a/x b/x\n{mode}\nindex 000..123\n").Files);
        Assert.Equal(oldPath, file.OldPath);
        Assert.Equal(newPath, file.NewPath);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void GitBinaryFilesAreRecognized()
    {
        var file = Assert.Single(Patch.Parse("diff --git a/x b/x\nindex 123..456\nBinary files a/x and b/x differ\n").Files);
        Assert.True(file.IsBinary);
        Assert.Equal("x", file.OldPath);
        Assert.Equal("x", file.NewPath);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GitHeadersAndNullPaths(string ending)
    {
        var patch = Patch.Parse(string.Join(ending, "preamble", "diff --git a/x b/x", "new file mode 100644", "index 000..123", "--- /dev/null", "+++ b/x", "@@ -0,0 +1 @@", "+hello", "\\ No newline at end of file", ""));
        var file = Assert.Single(patch.Files);
        Assert.Null(file.OldPath);
        Assert.Equal("x", file.NewPath);
        Assert.True(Assert.Single(Assert.Single(file.Hunks).Lines).NoNewline);
        Assert.Equal("hello", PatchApplier.Apply("", file).Text);
    }

    [Fact]
    public void GitRenameWithQuotedPathsAndTextChange()
    {
        var file = Assert.Single(Patch.Parse("diff --git \"a/old name\" \"b/new name\"\nsimilarity index 75%\nrename from old name\nrename to new name\nindex 123..456 100644\n--- \"a/old name\"\n+++ \"b/new name\"\n@@ -1 +1 @@\n-old\n+new\n").Files);
        Assert.Equal("old name", file.OldPath);
        Assert.Equal("new name", file.NewPath);
        Assert.Equal("new\n", PatchApplier.Apply("old\n", file).Text);
    }

    [Fact]
    public void PlainHeadersAllowTimestampsAndPreamble()
    {
        var file = Assert.Single(Patch.Parse("text before first header\n--- old.txt\t2026-01-01 12:00:00\n+++ new.txt\t2026-01-02 12:00:00\n@@ -1 +1 @@\n-old\n+new\n").Files);
        Assert.Equal("old.txt", file.OldPath);
        Assert.Equal("new.txt", file.NewPath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MissingNewlineMarkersBelongToTheirSide(bool oldMissing, bool newMissing)
    {
        var text = "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n"
            + (oldMissing ? "\\ No newline at end of file\n" : "") + "+new\n"
            + (newMissing ? "\\ No newline at end of file\n" : "");
        var file = Assert.Single(Patch.Parse(text).Files);
        var lines = Assert.Single(file.Hunks).Lines;
        Assert.Equal(oldMissing, lines[0].NoNewline);
        Assert.Equal(newMissing, lines[1].NoNewline);
        var left = "old" + (oldMissing ? "" : "\n");
        var right = "new" + (newMissing ? "" : "\n");
        Assert.Equal(right, PatchApplier.Apply(left, file).Text);
        Assert.Equal(left, PatchApplier.Apply(right, file, new PatchApplyOptions { Reverse = true }).Text);
    }

    [Theory]
    [InlineData("--- a/x\n+++ b/x\n@@ -1,2 +1 @@\n-a\n+b\n", "Patch line 6: hunk says 2 old lines but has 1")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1 +1,2 @@\n-a\n+b\n", "Patch line 6: hunk says 2 new lines but has 1")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n+extra\n", "Patch line 6: extra hunk body line after declared counts")]
    [InlineData("--- a/x\n+++ b/x\n@@ broken\n", "Patch line 3: invalid hunk header")]
    [InlineData("--- a/x\n@@ -1 +1 @@\n", "Patch line 2: expected +++ file header")]
    [InlineData("--- a/x\n", "Patch line 2: expected +++ file header")]
    public void MalformedInputNamesLineAndReason(string text, string message)
    {
        Assert.Equal(message, Assert.Throws<FormatException>(() => Patch.Parse(text)).Message);
    }
}
