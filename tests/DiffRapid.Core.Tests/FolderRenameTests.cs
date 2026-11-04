using System.Diagnostics;
using System.Text;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class FolderRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-renames-" + Guid.NewGuid());
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public FolderRenameTests() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); }
    public void Dispose() => Directory.Delete(_root, true);

    private void Write(bool left, string path, string text)
    {
        var full = Path.Combine(left ? Left : Right, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private FolderCompareResult Compare(double? similarity = null, bool detect = true) =>
        FolderComparer.Compare(Left, Right, new() { DetectRenames = detect, RenameSimilarityThreshold = similarity });

    private static FolderCompareItem Item(FolderCompareResult result, string path) => Assert.Single(result.Items, i => i.RelativePath == path);

    private static string Lines(params int[] numbers) => string.Join("\n", numbers.Select(n => $"line number {n}")) + "\n";

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("The quick brown fox jumps over the lazy dog, twice over again.", 0xB4DF9FBEDDA5E3BAUL)]
    public void XxHash64_matches_reference_values(string text, ulong expected)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, XxHash64.Compute(bytes));
        Assert.Equal(expected, XxHash64.Compute(new MemoryStream(bytes)));
    }

    [Fact]
    public void XxHash64_of_a_stream_matches_the_array_across_buffer_boundaries()
    {
        var random = new Random(7);
        foreach (var length in new[] { 31, 32, 33, 65535, 65536, 65537, 200003 })
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            Assert.Equal(XxHash64.Compute(bytes), XxHash64.Compute(new MemoryStream(bytes)));
            Assert.Equal(XxHash64.Compute(bytes, 1, length - 1), XxHash64.Compute(bytes.AsSpan(1).ToArray()));
        }
    }

    [Fact]
    public void Identical_files_under_new_names_are_renamed_or_moved_with_both_paths()
    {
        Write(true, "docs/old.txt", "renamed"); Write(false, "docs/new.txt", "renamed");
        Write(true, "src/code.cs", "moved"); Write(false, "lib/code.cs", "moved");
        Write(true, "gone.txt", "only left"); Write(false, "fresh.txt", "only right");
        var result = Compare();

        var old = Item(result, "docs/old.txt");
        Assert.Equal(FolderCompareStatus.Renamed, old.Status);
        Assert.Equal("docs/new.txt", old.CounterpartPath);
        Assert.Equal(1.0, old.Similarity);
        Assert.Equal("docs/old.txt", Item(result, "docs/new.txt").CounterpartPath);
        Assert.Equal(FolderCompareStatus.Renamed, Item(result, "docs/new.txt").Status);
        Assert.Equal(FolderCompareStatus.Different, Item(result, "docs").Status);

        Assert.Equal(FolderCompareStatus.Moved, Item(result, "src/code.cs").Status);
        Assert.Equal("lib/code.cs", Item(result, "src/code.cs").CounterpartPath);
        Assert.Equal(FolderCompareStatus.Moved, Item(result, "lib/code.cs").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "src").Status);

        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "gone.txt").Status);
        Assert.Equal(FolderCompareStatus.RightOnly, Item(result, "fresh.txt").Status);
        Assert.Equal(2, result.Counts[FolderCompareStatus.Renamed]);
        Assert.Equal(2, result.Counts[FolderCompareStatus.Moved]);
        Assert.Same(result.Root.Children.Single(c => c.RelativePath == "docs").Children[1], old);
    }

    [Fact]
    public void Rename_detection_is_off_by_default()
    {
        Write(true, "old.txt", "same"); Write(false, "new.txt", "same");
        var result = FolderComparer.Compare(Left, Right);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "old.txt").Status);
        Assert.Null(Item(result, "old.txt").CounterpartPath);
        Assert.Equal(0, result.Counts[FolderCompareStatus.Renamed]);
    }

    [Fact]
    public void Ambiguous_duplicates_stay_unpaired_unless_their_names_match()
    {
        Write(true, "x/a.txt", "dup"); Write(true, "y/b.txt", "dup"); Write(false, "z/c.txt", "dup");
        Write(true, "p/f.txt", "pair"); Write(true, "q/g.txt", "pair");
        Write(false, "r/f.txt", "pair"); Write(false, "s/g.txt", "pair");
        Write(true, "empty-left.txt", ""); Write(false, "empty-right.txt", "");
        var result = Compare();

        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "x/a.txt").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "y/b.txt").Status);
        Assert.Equal(FolderCompareStatus.RightOnly, Item(result, "z/c.txt").Status);
        Assert.Equal("r/f.txt", Item(result, "p/f.txt").CounterpartPath);
        Assert.Equal("s/g.txt", Item(result, "q/g.txt").CounterpartPath);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "empty-left.txt").Status);
        Assert.Equal(FolderCompareStatus.RightOnly, Item(result, "empty-right.txt").Status);
    }

    [Fact]
    public void One_remaining_duplicate_on_each_side_pairs_after_name_matches()
    {
        Write(true, "a/same.txt", "dup"); Write(true, "a/other.txt", "dup");
        Write(false, "b/same.txt", "dup"); Write(false, "b/renamed.txt", "dup");
        var result = Compare();
        Assert.Equal("b/same.txt", Item(result, "a/same.txt").CounterpartPath);
        Assert.Equal("b/renamed.txt", Item(result, "a/other.txt").CounterpartPath);
    }

    [Fact]
    public void Similar_text_files_pair_above_the_threshold_and_the_best_match_wins()
    {
        Write(true, "report.txt", Lines(1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
        Write(false, "close.txt", Lines(1, 2, 3, 4, 5, 6, 7, 8, 9, 11));
        Write(false, "far.txt", Lines(1, 2, 3, 4, 5, 6, 7, 8, 12, 13));
        File.WriteAllBytes(Path.Combine(Left, "data.bin"), new byte[] { 1, 0, 2, 3 });
        File.WriteAllBytes(Path.Combine(Right, "data2.bin"), new byte[] { 1, 0, 2, 4 });

        var result = Compare(0.6);
        var report = Item(result, "report.txt");
        Assert.Equal(FolderCompareStatus.Renamed, report.Status);
        Assert.Equal("close.txt", report.CounterpartPath);
        Assert.Equal(9 / 11.0, report.Similarity!.Value, 6);
        Assert.Equal(FolderCompareStatus.RightOnly, Item(result, "far.txt").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "data.bin").Status);

        Assert.Equal(FolderCompareStatus.LeftOnly, Item(Compare(0.9), "report.txt").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(Compare(), "report.txt").Status);
    }

    [Fact]
    public void Similarity_ignores_blank_lines_and_line_order_and_endings()
    {
        Write(true, "a.txt", "alpha\r\nbeta\r\n\r\ngamma\r\n");
        Write(false, "b.txt", "gamma\n  alpha\nbeta\n\n\n");
        var item = Item(Compare(0.99), "a.txt");
        Assert.Equal(FolderCompareStatus.Renamed, item.Status);
        Assert.Equal(1.0, item.Similarity);
    }

    [Theory]
    [InlineData("same", "same", FolderCompareStatus.Identical)]
    [InlineData("abcd", "abce", FolderCompareStatus.Different)]
    [InlineData("abc", "abcd", FolderCompareStatus.Different)]
    public void Hash_method_compares_size_then_hash(string left, string right, FolderCompareStatus expected)
    {
        Write(true, "f.txt", left); Write(false, "f.txt", right);
        var result = FolderComparer.Compare(Left, Right, new() { Method = FolderCompareMethod.Hash });
        Assert.Equal(expected, Item(result, "f.txt").Status);
    }

    private (int code, string output) Run(params string[] args)
    {
        var cli = typeof(DiffRapid.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(cli); start.ArgumentList.Add("dir"); start.ArgumentList.Add(Left); start.ArgumentList.Add(Right);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    [Fact]
    public void Cli_lists_rename_pairs_once_and_validates_similarity()
    {
        Write(true, "old.txt", "contents"); Write(false, "new.txt", "contents");
        Write(true, "report.txt", Lines(1, 2, 3, 4, 5)); Write(false, "summary.txt", Lines(1, 2, 3, 4, 6));

        var plain = Run();
        Assert.Contains("<  old.txt", plain.output);

        var renames = Run("--renames", "--only", "different");
        Assert.Equal(1, renames.code);
        Assert.Contains("R  old.txt -> new.txt", renames.output);
        Assert.DoesNotContain("new.txt -> old.txt", renames.output);
        Assert.DoesNotContain("report.txt ->", renames.output);

        var similar = Run("--similarity", "0.5");
        Assert.Contains("R  report.txt -> summary.txt (67% similar)", similar.output);

        Assert.Equal(2, Run("--similarity", "2").code);
        Assert.Equal(1, Run("--method", "hash").code);
    }
}
