using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;
using DiffRapid.Core.Patching;

namespace DiffRapid.Core.Tests;

public sealed class PatchCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-patch-" + Guid.NewGuid());

    public PatchCliTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string At(string path) => Path.Combine(_root, path);

    [Fact]
    public async Task FolderPatchReproducesRightAndReverses()
    {
        CreateFolders();
        CopyFolder("left", "copy");
        var original = Snapshot(At("copy"));
        Assert.Equal(1, await Program.Main(["patch", At("left"), At("right"), "-o", At("patch")]));
        Assert.Equal(0, await Program.Main(["patch", At("left"), At("left"), "-o", At("same.patch")]));
        Assert.Equal("", File.ReadAllText(At("same.patch")));
        Assert.Equal(0, await Program.Main(["apply", At("patch"), "--dir", At("copy"), "--dry-run"]));
        AssertSnapshot(original, At("copy"));
        Assert.Equal(0, await Program.Main(["apply", At("patch"), "--dir", At("copy")]));
        AssertSnapshot(Snapshot(At("right")), At("copy"));
        Assert.Equal(0, await Program.Main(["apply", At("patch"), "--dir", At("copy"), "--reverse"]));
        AssertSnapshot(original, At("copy"));
        AssertSnapshot(original, At("left"));
    }

    [Fact]
    public async Task FolderPatchReportsBinaryAndAppliesTextOverCopy()
    {
        CreateFolders();
        // The binary file sorts first: it is skipped and every text change after it still applies.
        File.WriteAllBytes(At("left/aa-binary"), [0, 1]);
        File.WriteAllBytes(At("right/aa-binary"), [0, 2]);
        CopyFolder("left", "copy");
        var original = Snapshot(At("copy"));
        using var writer = new StringWriter();
        FolderPatchWriter.Write(writer, FolderComparer.Compare(At("left"), At("right")), CompareOptions.Default);
        var patch = writer.ToString();
        Assert.Contains("Binary files a/aa-binary and b/aa-binary differ", patch);
        File.WriteAllText(At("patch"), patch);
        Assert.Equal(1, await Program.Main(["apply", At("patch"), "--dir", At("copy"), "--dry-run"]));
        AssertSnapshot(original, At("copy"));
        Assert.Equal(1, await Program.Main(["apply", At("patch"), "--dir", At("copy")]));
        var expected = Snapshot(At("right"));
        expected["aa-binary"] = original["aa-binary"];
        AssertSnapshot(expected, At("copy"));
        AssertSnapshot(original, At("left"));
    }

    [Fact]
    public async Task FailuresLeaveWholeFileUntouchedWhileOtherFilesApply()
    {
        File.WriteAllText(At("x"), "a\nwrong\n");
        File.WriteAllText(At("y"), "old\n");
        File.WriteAllText(At("patch"), "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n@@ -2 +2 @@\n-c\n+d\n--- a/y\n+++ b/y\n@@ -1 +1 @@\n-old\n+new\n");
        Assert.Equal(1, await Program.Main(["apply", At("patch"), "--dir", _root, "--fuzz", "0"]));
        Assert.Equal("a\nwrong\n", File.ReadAllText(At("x")));
        Assert.Equal("new\n", File.ReadAllText(At("y")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/rooted")]
    public async Task UnsafePathsAreRejectedBeforeAnyWrites(string unsafePath)
    {
        File.WriteAllText(At("safe"), "old\n");
        File.WriteAllText(At("patch"), "--- a/safe\n+++ b/safe\n@@ -1 +1 @@\n-old\n+new\n"
            + $"--- /dev/null\n+++ {unsafePath}\n@@ -0,0 +1 @@\n+bad\n");
        var original = Snapshot(_root);
        Assert.Equal(2, await Program.Main(["apply", At("patch"), "--dir", _root]));
        AssertSnapshot(original, _root);
    }

    [Fact]
    public async Task ExcessiveFuzzIsAUsageError()
    {
        File.WriteAllText(At("patch"), "");
        Assert.Equal(2, await Program.Main(["apply", At("patch"), "--fuzz", "3"]));
    }

    private void CreateFolders()
    {
        Directory.CreateDirectory(At("left/sub"));
        Directory.CreateDirectory(At("right/sub"));
        File.WriteAllText(At("left/sub/change"), "old\r\n", Encoding.Unicode);
        File.WriteAllText(At("right/sub/change"), "new\r\n", Encoding.Unicode);
        File.WriteAllText(At("left/remove"), "remove\n");
        File.WriteAllText(At("left/empty-remove"), "");
        File.WriteAllText(At("right/add"), "add");
        File.WriteAllText(At("right/empty-add"), "");
        File.WriteAllText(At("left/same"), "unchanged\n");
        File.WriteAllText(At("right/same"), "unchanged\n");
    }

    [Fact]
    public void RenamedAndMovedFilesAreWrittenAsGitRenames()
    {
        Directory.CreateDirectory(At("left/docs"));
        Directory.CreateDirectory(At("right/notes"));
        File.WriteAllText(At("left/old.txt"), "same\n");
        File.WriteAllText(At("right/new.txt"), "same\n");
        File.WriteAllText(At("left/docs/guide.txt"), "one\ntwo\nthree\nfour\n");
        File.WriteAllText(At("right/notes/guide.txt"), "one\ntwo\nthree\nFOUR\n");
        var options = new FolderCompareOptions { DetectRenames = true, RenameSimilarityThreshold = 0.5 };
        var result = FolderComparer.Compare(At("left"), At("right"), options);
        Assert.Contains(result.Items, i => i.Status == FolderCompareStatus.Renamed);
        Assert.Contains(result.Items, i => i.Status == FolderCompareStatus.Moved);
        using var writer = new StringWriter();
        FolderPatchWriter.Write(writer, result, CompareOptions.Default);
        var text = writer.ToString();
        Assert.Contains("rename from old.txt\nrename to new.txt\n", text);
        Assert.Contains("rename from docs/guide.txt\nrename to notes/guide.txt\n", text);

        CopyFolder("left", "copy");
        var plan = FolderPatchApplier.Plan(Patch.Parse(text), At("copy"));
        Assert.Equal(2, plan.Files.Count);
        Assert.All(FolderPatchApplier.Apply(plan), r => Assert.True(r.Written, r.Error));
        Directory.Delete(At("copy/docs"));
        AssertSnapshot(Snapshot(At("right")), At("copy"));
    }

    [Fact]
    public void PathsWithTabsNewlinesQuotesAndBackslashesRoundTrip()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var names = new[] { "tab\tname.txt", "new\nline.txt", "quote\"name.txt", "back\\slash.txt", "--- fake.txt" };
        Directory.CreateDirectory(At("left"));
        Directory.CreateDirectory(At("right"));
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(At("left"), name), "old\n");
            File.WriteAllText(Path.Combine(At("right"), name), "new\n");
        }

        using var writer = new StringWriter();
        FolderPatchWriter.Write(writer, FolderComparer.Compare(At("left"), At("right")), CompareOptions.Default);
        var patch = Patch.Parse(writer.ToString());
        Assert.Equal(names.Order(), patch.Files.Select(f => f.NewPath!).Order());
        Assert.All(patch.Files, f => Assert.Equal(f.OldPath, f.NewPath));

        CopyFolder("left", "copy");
        Assert.All(FolderPatchApplier.Apply(FolderPatchApplier.Plan(patch, At("copy"))), r => Assert.True(r.Written, r.Error));
        AssertSnapshot(Snapshot(At("right")), At("copy"));
    }

    [Theory]
    [InlineData("\"a/x\\001y\"", "x\u0001y")]
    [InlineData("\"a/caf\\303\\251\"", "café")]
    [InlineData("\"a/q\\\"t\\\\b\"\t2020-01-01", "q\"t\\b")]
    public void QuotedPatchPathsAreUnescapedLikeGit(string quoted, string expected)
    {
        var file = Assert.Single(Patch.Parse("--- " + quoted + "\n+++ " + quoted + "\n@@ -1 +1 @@\n-a\n+b\n").Files);
        Assert.Equal(expected, file.OldPath);
    }

    [Theory]
    [InlineData("\"a/unterminated")]
    [InlineData("\"a/bad\\q\"")]
    [InlineData("\"a/x\" trailing")]
    [InlineData("\"a/\\400\"")]
    public void InvalidQuotedPatchPathsAreRejected(string quoted)
    {
        Assert.Throws<FormatException>(() => Patch.Parse("--- " + quoted + "\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n"));
    }

    private void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(At(target));
        foreach (var file in Directory.GetFiles(At(source), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(At(target), Path.GetRelativePath(At(source), file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllBytes);

    private static void AssertSnapshot(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Snapshot(root);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var pair in expected)
        {
            Assert.Equal(pair.Value, actual[pair.Key]);
        }
    }
}
