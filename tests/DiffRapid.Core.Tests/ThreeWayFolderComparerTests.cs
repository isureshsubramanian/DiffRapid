using System.Diagnostics;
using System.Text.Json;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class ThreeWayFolderComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-three-way-" + Guid.NewGuid());
    private string Base => Path.Combine(_root, "base");
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public ThreeWayFolderComparerTests()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>Writes a file into each tree whose text is not null.</summary>
    private void Write(string path, string? baseText, string? leftText, string? rightText)
    {
        foreach (var (folder, text) in new[] { (Base, baseText), (Left, leftText), (Right, rightText) })
        {
            if (text is null)
            {
                continue;
            }

            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    private ThreeWayFolderResult Compare(FolderCompareOptions? options = null) => ThreeWayFolderComparer.Compare(Base, Left, Right, options);

    private static ThreeWayFolderStatus Status(ThreeWayFolderResult result, string path) =>
        Assert.Single(result.Items, i => i.RelativePath == path).Status;

    [Theory]
    [InlineData("a", "a", "a", ThreeWayFolderStatus.Unchanged)]
    [InlineData("a", "b", "a", ThreeWayFolderStatus.ChangedLeft)]
    [InlineData("a", "a", "c", ThreeWayFolderStatus.ChangedRight)]
    [InlineData("a", "x", "x", ThreeWayFolderStatus.ChangedBothIdentically)]
    [InlineData("a", "x", "y", ThreeWayFolderStatus.Conflict)]
    [InlineData(null, "n", null, ThreeWayFolderStatus.AddedLeft)]
    [InlineData(null, null, "n", ThreeWayFolderStatus.AddedRight)]
    [InlineData(null, "z", "z", ThreeWayFolderStatus.AddedBothIdentically)]
    [InlineData(null, "1", "2", ThreeWayFolderStatus.Conflict)]
    [InlineData("a", null, "a", ThreeWayFolderStatus.RemovedLeft)]
    [InlineData("a", "a", null, ThreeWayFolderStatus.RemovedRight)]
    [InlineData("a", null, null, ThreeWayFolderStatus.RemovedBoth)]
    [InlineData("a", null, "b", ThreeWayFolderStatus.Conflict)]
    [InlineData("a", "b", null, ThreeWayFolderStatus.Conflict)]
    public void Files_get_a_three_way_status(string? baseText, string? leftText, string? rightText, ThreeWayFolderStatus expected)
    {
        Write("file.txt", baseText, leftText, rightText);
        var result = Compare();
        Assert.Equal(expected, Status(result, "file.txt"));
        Assert.Equal(1, result.Counts[expected]);
    }

    [Fact]
    public void Folders_summarise_their_contents()
    {
        Write("same/f.txt", "a", "a", "a");
        Write("mixed/l.txt", "a", "b", "a");
        Write("mixed/r.txt", "a", "a", "c");
        Write("leftonly/l.txt", "a", "b", "a");
        Write("clash/x.txt", "a", "b", "c");
        Write("clash/fine.txt", "a", "b", "a");
        Write("newdir/deep/n.txt", null, "n", null);
        Write("olddir/o.txt", "o", null, null);
        var result = Compare();

        Assert.Equal(ThreeWayFolderStatus.Unchanged, Status(result, "same"));
        Assert.Equal(ThreeWayFolderStatus.ChangedBoth, Status(result, "mixed"));
        Assert.Equal(ThreeWayFolderStatus.ChangedLeft, Status(result, "leftonly"));
        Assert.Equal(ThreeWayFolderStatus.Conflict, Status(result, "clash"));
        Assert.Equal(ThreeWayFolderStatus.ChangedLeft, Status(result, "clash/fine.txt"));
        Assert.Equal(ThreeWayFolderStatus.AddedLeft, Status(result, "newdir"));
        Assert.Equal(ThreeWayFolderStatus.AddedLeft, Status(result, "newdir/deep/n.txt"));
        Assert.Equal(ThreeWayFolderStatus.RemovedBoth, Status(result, "olddir"));
        Assert.Equal(ThreeWayFolderStatus.RemovedBoth, Status(result, "olddir/o.txt"));
        Assert.Equal(ThreeWayFolderStatus.Conflict, result.Root.Status);

        Assert.Equal(result.Items.Select(i => i.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), result.Items.Select(i => i.RelativePath));
        Assert.Equal(result.Items.Count, result.Counts.Values.Sum());
        Assert.Equal(Path.GetFullPath(Base), result.BaseRoot);
        var leftOnlyFile = Assert.Single(result.Items, i => i.RelativePath == "newdir/deep/n.txt");
        Assert.True(leftOnlyFile.Left.Exists);
        Assert.False(leftOnlyFile.Base.Exists || leftOnlyFile.Right.Exists);
    }

    [Fact]
    public void A_file_replaced_by_a_folder_on_one_side_is_a_change_on_that_side()
    {
        Write("t", "file", null, "file");
        Write("t/inner.txt", null, "x", null);
        var result = Compare();
        Assert.Equal(ThreeWayFolderStatus.ChangedLeft, Status(result, "t"));
    }

    [Fact]
    public void Filters_skip_entries_and_options_are_shared()
    {
        Write("build.tmp", "a", "b", "c");
        Write("kept.txt", "a", "A", "a");
        var result = Compare(new() { ExcludeFiles = new[] { "*.tmp" }, TextOptions = Comparison.CompareOptions.Default with { IgnoreCase = true } });
        Assert.Equal(ThreeWayFolderStatus.Skipped, Status(result, "build.tmp"));
        Assert.Equal(ThreeWayFolderStatus.Unchanged, Status(result, "kept.txt"));
        Assert.Equal(ThreeWayFolderStatus.Unchanged, result.Root.Status);
    }

    [Fact]
    public void A_missing_root_is_an_error_and_cancellation_throws()
    {
        var result = ThreeWayFolderComparer.Compare(Path.Combine(_root, "missing"), Left, Right);
        Assert.Equal(ThreeWayFolderStatus.Error, result.Root.Status);
        Assert.NotNull(result.Root.Error);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ThreeWayFolderComparer.Compare(Base, Left, Right, null, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => ThreeWayFolderComparer.Compare(null!, Left, Right));
    }

    private (int code, string output) Run(params string[] args)
    {
        var cli = typeof(DiffRapid.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("dir3");
        start.ArgumentList.Add(Base);
        start.ArgumentList.Add(Left);
        start.ArgumentList.Add(Right);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    [Fact]
    public void Cli_lists_three_way_changes_and_exits_one_on_conflicts()
    {
        Write("same.txt", "a", "a", "a");
        Write("left.txt", "a", "b", "a");
        Write("sub/added.txt", null, null, "n");
        var clean = Run();
        Assert.Equal(0, clean.code);
        Assert.Contains("changed left         left.txt", clean.output);
        Assert.Contains("added right          sub", clean.output);
        Assert.DoesNotContain("same.txt", clean.output);

        Write("clash.txt", "a", "b", "c");
        var conflicted = Run("--method", "hash");
        Assert.Equal(1, conflicted.code);
        Assert.Contains("conflict             clash.txt", conflicted.output);

        var json = Run("--json");
        using var document = JsonDocument.Parse(json.output);
        Assert.Equal("Conflict", document.RootElement.GetProperty("Root").GetProperty("Status").GetString());

        Assert.Equal(2, Run("extra").code);
    }
}
