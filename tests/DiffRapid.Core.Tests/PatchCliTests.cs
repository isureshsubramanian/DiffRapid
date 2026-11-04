using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;

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
    public void FolderPatchReproducesRightAndReverses()
    {
        CreateFolders();
        CopyFolder("left", "copy");
        var original = Snapshot(At("copy"));
        Assert.Equal(0, Program.Main(["patch", At("left"), At("right"), "-o", At("patch")]));
        Assert.Equal(0, Program.Main(["apply", At("patch"), "--dir", At("copy"), "--dry-run"]));
        AssertSnapshot(original, At("copy"));
        Assert.Equal(0, Program.Main(["apply", At("patch"), "--dir", At("copy")]));
        AssertSnapshot(Snapshot(At("right")), At("copy"));
        Assert.Equal(0, Program.Main(["apply", At("patch"), "--dir", At("copy"), "--reverse"]));
        AssertSnapshot(original, At("copy"));
        AssertSnapshot(original, At("left"));
    }

    [Fact]
    public void FolderPatchReportsBinaryAndAppliesTextOverCopy()
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
        Assert.Equal(1, Program.Main(["apply", At("patch"), "--dir", At("copy"), "--dry-run"]));
        AssertSnapshot(original, At("copy"));
        Assert.Equal(1, Program.Main(["apply", At("patch"), "--dir", At("copy")]));
        var expected = Snapshot(At("right"));
        expected["aa-binary"] = original["aa-binary"];
        AssertSnapshot(expected, At("copy"));
        AssertSnapshot(original, At("left"));
    }

    [Fact]
    public void FailuresLeaveWholeFileUntouchedWhileOtherFilesApply()
    {
        File.WriteAllText(At("x"), "a\nwrong\n");
        File.WriteAllText(At("y"), "old\n");
        File.WriteAllText(At("patch"), "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n@@ -2 +2 @@\n-c\n+d\n--- a/y\n+++ b/y\n@@ -1 +1 @@\n-old\n+new\n");
        Assert.Equal(1, Program.Main(["apply", At("patch"), "--dir", _root, "--fuzz", "0"]));
        Assert.Equal("a\nwrong\n", File.ReadAllText(At("x")));
        Assert.Equal("new\n", File.ReadAllText(At("y")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/rooted")]
    public void UnsafePathsAreRejectedBeforeAnyWrites(string unsafePath)
    {
        File.WriteAllText(At("safe"), "old\n");
        File.WriteAllText(At("patch"), "--- a/safe\n+++ b/safe\n@@ -1 +1 @@\n-old\n+new\n"
            + $"--- /dev/null\n+++ {unsafePath}\n@@ -0,0 +1 @@\n+bad\n");
        var original = Snapshot(_root);
        Assert.Equal(2, Program.Main(["apply", At("patch"), "--dir", _root]));
        AssertSnapshot(original, _root);
    }

    [Fact]
    public void ExcessiveFuzzIsAUsageError()
    {
        File.WriteAllText(At("patch"), "");
        Assert.Equal(2, Program.Main(["apply", At("patch"), "--fuzz", "3"]));
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
