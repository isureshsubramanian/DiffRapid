using System.Text;
using DiffRapid.Core.Patching;

namespace DiffRapid.Core.Tests;

public sealed class FolderPatchApplierTests : IDisposable
{
    private const string ChangeA = "--- a/a.txt\n+++ b/a.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-fpa-" + Guid.NewGuid());

    public FolderPatchApplierTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string At(string path) => Path.Combine(_root, path);

    private static Patch Parse(string text) => Patch.Parse(text);

    [Fact]
    public void PlanDoesNotWriteAndApplyWrites()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\n");
        var plan = FolderPatchApplier.Plan(Parse(ChangeA), _root);

        var file = Assert.Single(plan.Files);
        Assert.Equal(FilePatchStatus.Ready, file.Status);
        Assert.Equal(FilePatchAction.Modify, file.Action);
        Assert.Equal("a.txt", file.DisplayPath);
        Assert.Equal("one\nTWO\nthree\n", file.PatchedText);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(At("a.txt")));

        var result = Assert.Single(FolderPatchApplier.Apply(plan));
        Assert.True(result.Written);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(At("a.txt")));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void AppliedPatchPlansAsAlreadyApplied()
    {
        File.WriteAllText(At("a.txt"), "one\nTWO\nthree\n");
        var file = Assert.Single(FolderPatchApplier.Plan(Parse(ChangeA), _root).Files);
        Assert.Equal(FilePatchStatus.AlreadyApplied, file.Status);
        Assert.False(file.CanApply);
    }

    [Fact]
    public void MismatchFailsWithHunkMessage()
    {
        File.WriteAllText(At("a.txt"), "alpha\nbeta\ngamma\n");
        var file = Assert.Single(FolderPatchApplier.Plan(Parse(ChangeA), _root, new PatchApplyOptions { Fuzz = 0 }).Files);
        Assert.Equal(FilePatchStatus.Failed, file.Status);
        Assert.Equal("Hunk 1 did not match near line 1.", file.Message);
        Assert.False(FolderPatchApplier.ApplyFile(file).Written);
    }

    [Fact]
    public void MissingFileFails()
    {
        var file = Assert.Single(FolderPatchApplier.Plan(Parse(ChangeA), _root).Files);
        Assert.Equal(FilePatchStatus.Failed, file.Status);
        Assert.Equal("The file does not exist.", file.Message);
    }

    [Fact]
    public void AddsAndDeletesFiles()
    {
        File.WriteAllText(At("old.txt"), "gone\n");
        var patch = Parse(
            "--- /dev/null\n+++ b/sub/new.txt\n@@ -0,0 +1,1 @@\n+hello\n" +
            "--- a/old.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-gone\n");
        var plan = FolderPatchApplier.Plan(patch, _root);
        Assert.Equal([FilePatchAction.Add, FilePatchAction.Delete], plan.Files.Select(f => f.Action));
        Assert.All(plan.Files, f => Assert.Equal(FilePatchStatus.Ready, f.Status));

        Assert.All(FolderPatchApplier.Apply(plan), r => Assert.True(r.Written));
        Assert.Equal("hello\n", File.ReadAllText(At("sub/new.txt")));
        Assert.False(File.Exists(At("old.txt")));

        var again = FolderPatchApplier.Plan(patch, _root);
        Assert.All(again.Files, f => Assert.Equal(FilePatchStatus.AlreadyApplied, f.Status));
        Assert.True(again.AllApplicable);
    }

    [Fact]
    public void AddingOverDifferentContentFails()
    {
        File.WriteAllText(At("new.txt"), "other\n");
        var patch = Parse("--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,1 @@\n+hello\n");
        Assert.Equal(FilePatchStatus.Failed, Assert.Single(FolderPatchApplier.Plan(patch, _root).Files).Status);
    }

    [Fact]
    public void DeletingFileWithExtraContentFails()
    {
        File.WriteAllText(At("old.txt"), "gone\nkept\n");
        var patch = Parse("--- a/old.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-gone\n");
        Assert.Equal(FilePatchStatus.Failed, Assert.Single(FolderPatchApplier.Plan(patch, _root).Files).Status);
    }

    [Fact]
    public void RenameWritesNewPathAndRemovesOld()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\n");
        var patch = Parse("diff --git a/a.txt b/b.txt\n--- a/a.txt\n+++ b/b.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n");
        var plan = FolderPatchApplier.Plan(patch, _root);
        var file = Assert.Single(plan.Files);
        Assert.Equal(FilePatchAction.Rename, file.Action);
        Assert.True(Assert.Single(FolderPatchApplier.Apply(plan)).Written);
        Assert.False(File.Exists(At("a.txt")));
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(At("b.txt")));
    }

    [Fact]
    public void ReverseUndoesTheChange()
    {
        File.WriteAllText(At("a.txt"), "one\nTWO\nthree\n");
        var plan = FolderPatchApplier.Plan(Parse(ChangeA), _root, new PatchApplyOptions { Reverse = true });
        Assert.True(Assert.Single(FolderPatchApplier.Apply(plan)).Written);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(At("a.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    public void UnsafePathsAreRejected(string path)
    {
        var patch = Parse($"--- a/{path}\n+++ b/{path}\n@@ -1,1 +1,1 @@\n-x\n+y\n");
        var file = Assert.Single(FolderPatchApplier.Plan(patch, _root).Files);
        Assert.Equal(FilePatchStatus.Unsafe, file.Status);
        Assert.NotNull(file.Message);
    }

    [Fact]
    public void PathThroughJunctionIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(Path.GetTempPath(), "diffrapid-fpa-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "a.txt"), "one\ntwo\nthree\n");
            if (!TryCreateJunction(At("link"), outside))
            {
                return;
            }

            var patch = Parse(ChangeA.Replace("a/a.txt", "a/link/a.txt").Replace("b/a.txt", "b/link/a.txt"));
            Assert.Equal(FilePatchStatus.Unsafe, Assert.Single(FolderPatchApplier.Plan(patch, _root).Files).Status);
            Directory.Delete(At("link"));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void PlanFileIgnoresPatchNames()
    {
        File.WriteAllText(At("renamed.txt"), "one\ntwo\nthree\n");
        var patch = Assert.Single(Parse(ChangeA).Files);
        var file = FolderPatchApplier.PlanFile(patch, At("renamed.txt"));
        Assert.Equal(FilePatchStatus.Ready, file.Status);
        Assert.Equal(FilePatchAction.Modify, file.Action);
        Assert.True(FolderPatchApplier.ApplyFile(file).Written);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(At("renamed.txt")));
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16")]
    public void EncodingAndByteOrderMarkAreKept(string kind)
    {
        var encoding = kind == "utf-16" ? Encoding.Unicode : new UTF8Encoding(true);
        File.WriteAllBytes(At("a.txt"), [.. encoding.GetPreamble(), .. encoding.GetBytes("one\r\ntwo\r\nthree\r\n")]);
        var plan = FolderPatchApplier.Plan(Parse(ChangeA), _root);
        Assert.True(Assert.Single(FolderPatchApplier.Apply(plan)).Written);
        Assert.Equal([.. encoding.GetPreamble(), .. encoding.GetBytes("one\r\nTWO\r\nthree\r\n")], File.ReadAllBytes(At("a.txt")));
    }

    [Fact]
    public void SourceChangedSincePreviewIsNotWritten()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\n");
        var plan = FolderPatchApplier.Plan(Parse(ChangeA), _root);
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\nfour\n");
        var result = Assert.Single(FolderPatchApplier.Apply(plan));
        Assert.False(result.Written);
        Assert.Equal("The file changed since the preview.", result.Error);
        Assert.Equal("one\ntwo\nthree\nfour\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public void BinaryFileIsReported()
    {
        File.WriteAllBytes(At("a.txt"), [1, 0, 2]);
        Assert.Equal(FilePatchStatus.Binary, Assert.Single(FolderPatchApplier.Plan(Parse(ChangeA), _root).Files).Status);
    }

    [Fact]
    public void IncludeFilterLimitsWrites()
    {
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(At("c.txt"), "one\ntwo\nthree\n");
        var plan = FolderPatchApplier.Plan(Parse(ChangeA + ChangeA.Replace("a.txt", "c.txt")), _root);
        var results = FolderPatchApplier.Apply(plan, f => f.DisplayPath == "c.txt");
        Assert.Equal("c.txt", Assert.Single(results).File.DisplayPath);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(At("a.txt")));
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(At("c.txt")));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
