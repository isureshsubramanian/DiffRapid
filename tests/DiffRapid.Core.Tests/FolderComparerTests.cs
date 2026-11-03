using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class FolderComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-folders-" + Guid.NewGuid());
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public FolderComparerTests() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); }
    public void Dispose() => Directory.Delete(_root, true);

    private string Write(bool left, string path, string text)
    {
        var full = Path.Combine(left ? Left : Right, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private FolderCompareResult Compare(FolderCompareOptions? options = null, IProgress<FolderCompareItem>? progress = null,
        CancellationToken token = default) => FolderComparer.Compare(Left, Right, options, progress, token);
    private static FolderCompareItem Item(FolderCompareResult result, string path) => Assert.Single(result.Items, i => i.RelativePath == path);

    [Theory]
    [InlineData(FolderCompareMethod.FullContents, FolderCompareStatus.Different)]
    [InlineData(FolderCompareMethod.QuickContents, FolderCompareStatus.Identical)]
    [InlineData(FolderCompareMethod.BinaryContents, FolderCompareStatus.Different)]
    [InlineData(FolderCompareMethod.Date, FolderCompareStatus.Identical)]
    [InlineData(FolderCompareMethod.DateAndSize, FolderCompareStatus.Identical)]
    [InlineData(FolderCompareMethod.Size, FolderCompareStatus.Identical)]
    [InlineData(FolderCompareMethod.Existence, FolderCompareStatus.Identical)]
    public void Methods_have_distinct_semantics(FolderCompareMethod method, FolderCompareStatus expected)
    {
        var l = Write(true, "a", "one"); var r = Write(false, "a", "two");
        var time = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(l, time); File.SetLastWriteTimeUtc(r, time);
        Assert.Equal(expected, Item(Compare(new() { Method = method }), "a").Status);
    }

    [Theory]
    [InlineData(FolderCompareMethod.FullContents)]
    [InlineData(FolderCompareMethod.QuickContents)]
    [InlineData(FolderCompareMethod.BinaryContents)]
    [InlineData(FolderCompareMethod.Date)]
    [InlineData(FolderCompareMethod.DateAndSize)]
    [InlineData(FolderCompareMethod.Size)]
    [InlineData(FolderCompareMethod.Existence)]
    public void All_methods_recognize_identical_files(FolderCompareMethod method)
    {
        Write(true, "a", "same"); Write(false, "a", "same");
        var result = Compare(new() { Method = method });
        Assert.Equal(FolderCompareStatus.Identical, result.Root.Status);
        Assert.Equal(1, result.Counts[FolderCompareStatus.Identical]);
    }

    [Fact]
    public void Tolerance_is_inclusive_and_quick_falls_back_to_bytes()
    {
        var l = Write(true, "a", "one"); var r = Write(false, "a", "one");
        var time = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(l, time); File.SetLastWriteTimeUtc(r, time.AddSeconds(2));
        Assert.Equal(FolderCompareStatus.Identical, Compare(new() { Method = FolderCompareMethod.Date }).Root.Status);
        Assert.Equal(FolderCompareStatus.Different, Compare(new() { Method = FolderCompareMethod.Date, TimeTolerance = TimeSpan.FromSeconds(1) }).Root.Status);
        File.SetLastWriteTimeUtc(r, time.AddSeconds(10));
        Assert.Equal(FolderCompareStatus.Identical, Compare(new() { Method = FolderCompareMethod.QuickContents }).Root.Status);
        File.WriteAllText(r, "two"); File.SetLastWriteTimeUtc(r, time.AddSeconds(10));
        Assert.Equal(FolderCompareStatus.Different, Compare(new() { Method = FolderCompareMethod.QuickContents }).Root.Status);
    }

    [Fact]
    public void Reports_one_sided_trees_and_type_conflicts()
    {
        Write(true, "only-left/child", "a"); Write(false, "only-right", "b");
        Write(true, "conflict", "file"); Directory.CreateDirectory(Path.Combine(Right, "conflict"));
        var result = Compare();
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "only-left").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "only-left/child").Status);
        Assert.Equal(FolderCompareStatus.RightOnly, Item(result, "only-right").Status);
        Assert.Equal(FolderCompareStatus.TypeConflict, Item(result, "conflict").Status);
        Assert.False(Item(result, "only-right").Left.Exists);
    }

    [Fact]
    public void Nested_status_and_progress_are_complete_and_ordered()
    {
        Write(true, "z/n/a", "a"); Write(false, "z/n/a", "b");
        Write(true, "A", "same"); Write(false, "A", "same");
        var reported = new List<FolderCompareItem>();
        var result = Compare(new() { MaxDegreeOfParallelism = 2 }, new Reporter(reported.Add));
        Assert.Equal(new[] { "A", "z", "z/n", "z/n/a" }, result.Items.Select(i => i.RelativePath));
        Assert.Equal(FolderCompareStatus.Different, Item(result, "z/n").Status);
        Assert.True(reported.FindIndex(i => i.RelativePath == "z/n/a") < reported.FindIndex(i => i.RelativePath == "z/n"));
        Assert.Same(result.Root, reported.Last());
        Assert.Equal(result.Items.Count + 1, reported.Count);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData("packages")]
    [InlineData("TestResults")]
    public void Default_excludes_prune_directories(string folder)
    {
        Write(true, folder + "/hidden", "a");
        Assert.Equal(FolderCompareStatus.Skipped, Item(Compare(), folder).Status);
        Assert.Equal(FolderCompareStatus.Identical, Compare().Root.Status);
        Assert.Equal(2, Compare(new() { DefaultExcludes = false }).Items.Count);
    }

    [Fact]
    public void File_and_folder_masks_apply_to_names_and_relative_paths()
    {
        Write(true, "src/a.txt", "a"); Write(true, "src/b.cs", "b");
        Write(true, "src/skip.txt", "c"); Write(true, "vendor/a.txt", "a");
        var result = Compare(new() { IncludeFiles = ["**/*.txt"], ExcludeFiles = ["skip.*"], ExcludeFolders = ["vendor"] });
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "src/a.txt").Status);
        Assert.Equal(FolderCompareStatus.Skipped, Item(result, "src/b.cs").Status);
        Assert.Equal(FolderCompareStatus.Skipped, Item(result, "src/skip.txt").Status);
        Assert.Empty(Item(result, "vendor").Children);
        Assert.Equal(FolderCompareStatus.Skipped, Item(Compare(new() { IncludeFolders = ["vendor"] }), "src").Status);
    }

    [Fact]
    public void Full_contents_respects_text_options_and_counts_hunks()
    {
        Write(true, "a", "hello world\n"); Write(false, "a", "hello  world\n");
        var options = new FolderCompareOptions { TextOptions = new CompareOptions { Whitespace = WhitespaceMode.IgnoreAll } };
        Assert.Equal(FolderCompareStatus.Identical, Compare(options).Root.Status);
        Assert.Equal(0L, Item(Compare(options), "a").DifferenceCount);
        Assert.Equal(1L, Item(Compare(), "a").DifferenceCount);
        Assert.Equal(FolderCompareStatus.Different, Compare(options with { MaxTextFileSize = 1 }).Root.Status);
        Assert.Null(Item(Compare(options with { MaxTextFileSize = 1 }), "a").DifferenceCount);
    }

    [Fact]
    public void Binary_probe_and_utf16_bom_follow_reader_behavior()
    {
        var l = Write(true, "a", ""); var r = Write(false, "a", "");
        File.WriteAllBytes(l, [1, 0, 2]); File.WriteAllBytes(r, [1, 0, 3]);
        Assert.Null(Item(Compare(), "a").DifferenceCount);
        File.WriteAllText(l, "a b", System.Text.Encoding.Unicode);
        File.WriteAllText(r, "ab", System.Text.Encoding.Unicode);
        Assert.Equal(FolderCompareStatus.Identical, Compare(new() { TextOptions = new() { Whitespace = WhitespaceMode.IgnoreAll } }).Root.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Binary_reads_beyond_a_single_buffer(bool stop)
    {
        var l = Write(true, "a", ""); var r = Write(false, "a", "");
        var bytes = new byte[150000]; File.WriteAllBytes(l, bytes); bytes[140000] = 1; File.WriteAllBytes(r, bytes);
        Assert.Equal(FolderCompareStatus.Different, Compare(new() { Method = FolderCompareMethod.BinaryContents, StopAtFirstDifference = stop }).Root.Status);
    }

    [Fact]
    public void Nonrecursive_scan_lists_subfolders_by_existence_only()
    {
        Write(true, "nested/a", "a"); Write(false, "nested/a", "different");
        Write(true, "leftonly/b", "b");
        var result = Compare(new() { Recursive = false });
        Assert.Empty(Item(result, "nested").Children);
        Assert.Equal(FolderCompareStatus.Identical, Item(result, "nested").Status);
        Assert.Equal(FolderCompareStatus.LeftOnly, Item(result, "leftonly").Status);
    }

    [Fact]
    public void Name_matching_defaults_to_ignore_case()
    {
        Write(true, "File", "same"); Write(false, "file", "same");
        Assert.Equal(FolderCompareStatus.Identical, Compare().Root.Status);
        Assert.Equal(2, Compare(new() { CaseSensitive = true }).Items.Count);
    }

    [Fact]
    public void Cancellation_before_and_during_scan_throws()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Compare(token: source.Token));
        using var during = new CancellationTokenSource();
        Write(true, "a", "a"); Write(true, "b", "b");
        Assert.Throws<OperationCanceledException>(() => Compare(new() { MaxDegreeOfParallelism = 1 }, new Reporter(_ => during.Cancel()), during.Token));
    }

    [Fact]
    public void Missing_roots_are_error_items()
    {
        Directory.Delete(Right);
        var result = Compare();
        Assert.Equal(FolderCompareStatus.Error, result.Root.Status);
        Assert.NotNull(result.Root.Error);
    }

    [Fact]
    public void Vanishing_entry_is_reported_as_error()
    {
        Write(true, "a", "a"); var doomed = Write(true, "b", "b");
        var result = Compare(new() { MaxDegreeOfParallelism = 1 }, new Reporter(i => { if (i.RelativePath == "a") File.Delete(doomed); }));
        Assert.Equal(FolderCompareStatus.Error, Item(result, "b").Status);

        // One unreadable entry makes its folder different; it does not fail the whole comparison.
        Assert.Equal(FolderCompareStatus.Different, result.Root.Status);
    }

    [Fact]
    public void Symlinks_are_items_and_are_not_traversed()
    {
        Write(true, "target/child", "a");
        var link = Path.Combine(Left, "link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(Left, "target")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symbolic links is not permitted: " + ex.Message);
        }
        var item = Item(Compare(), "link");
        Assert.Equal(FolderItemKind.Link, item.Kind);
        Assert.Empty(item.Children);
        Assert.Equal(FolderCompareStatus.LeftOnly, item.Status);
        Assert.Single(Item(Compare(new() { FollowSymlinks = true }), "link").Children);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void Invalid_resource_limits_are_rejected(int parallelism, long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compare(new() { MaxDegreeOfParallelism = parallelism, MaxTextFileSize = size }));
    }

    private sealed class Reporter(Action<FolderCompareItem> report) : IProgress<FolderCompareItem>
    {
        public void Report(FolderCompareItem item) => report(item);
    }
}
