using System.Diagnostics;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class FolderStreamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-stream-" + Guid.NewGuid());
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public FolderStreamingTests() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); }
    public void Dispose() => Directory.Delete(_root, true);

    private void Write(bool left, string path, string text)
    {
        var full = Path.Combine(left ? Left : Right, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed class Collector<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [Fact]
    public void Streaming_reports_every_item_once_with_a_running_count_and_children_before_their_folder()
    {
        Write(true, "a/one.txt", "1"); Write(false, "a/one.txt", "1");
        Write(true, "a/two.txt", "2"); Write(false, "a/two.txt", "x");
        Write(true, "b/deep/three.txt", "3");
        Write(false, "c.txt", "c");
        var collector = new Collector<FolderCompareProgress>();
        var result = FolderComparer.CompareStreaming(Left, Right, new() { MaxDegreeOfParallelism = 1 }, collector);

        Assert.Equal(result.Items.Count + 1, collector.Reports.Count);
        Assert.Equal(Enumerable.Range(1, collector.Reports.Count), collector.Reports.Select(r => r.CompletedCount));
        Assert.All(collector.Reports, r => Assert.False(r.IsRevision));
        Assert.Equal(string.Empty, collector.Reports[^1].Item.RelativePath);
        var order = collector.Reports.Select(r => r.Item.RelativePath).ToList();
        Assert.Equal(
            new[] { "a/one.txt", "a/two.txt", "a", "b/deep/three.txt", "b/deep", "b", "c.txt", "" },
            order);
        Assert.Equal(result.Items.Select(i => i.RelativePath).OrderBy(p => p), order.Take(order.Count - 1).OrderBy(p => p));
    }

    [Fact]
    public void Streaming_in_parallel_still_counts_each_item_once()
    {
        for (var i = 0; i < 40; i++)
        {
            Write(true, $"d{i}/f.txt", "same"); Write(false, $"d{i}/f.txt", i % 2 == 0 ? "same" : "other");
        }

        var collector = new Collector<FolderCompareProgress>();
        var result = FolderComparer.CompareStreaming(Left, Right, new() { MaxDegreeOfParallelism = 8 }, collector);
        Assert.Equal(81, collector.Reports.Count);
        Assert.Equal(Enumerable.Range(1, 81), collector.Reports.Select(r => r.CompletedCount));
        Assert.Equal(40, result.Counts[FolderCompareStatus.Different]);
        foreach (var report in collector.Reports.Where(r => r.Item.Kind == FolderItemKind.File))
        {
            var folder = report.Item.RelativePath.Split('/')[0];
            Assert.True(collector.Reports.FindIndex(r => r.Item.RelativePath == folder) > collector.Reports.IndexOf(report));
        }
    }

    [Fact]
    public void Streaming_reports_rename_pairs_again_as_revisions_and_the_old_overload_still_works()
    {
        Write(true, "old.txt", "contents"); Write(false, "new.txt", "contents");
        var collector = new Collector<FolderCompareProgress>();
        var result = FolderComparer.CompareStreaming(Left, Right, new() { DetectRenames = true }, collector);

        var revisions = collector.Reports.Where(r => r.IsRevision).ToList();
        Assert.Equal(new[] { "old.txt", "new.txt" }, revisions.Select(r => r.Item.RelativePath));
        Assert.All(revisions, r => Assert.Equal(FolderCompareStatus.Renamed, r.Item.Status));
        Assert.All(revisions, r => Assert.Equal(3, r.CompletedCount));
        Assert.Equal(2, result.Counts[FolderCompareStatus.Renamed]);

        var items = new Collector<FolderCompareItem>();
        FolderComparer.Compare(Left, Right, new() { DetectRenames = true }, items);
        Assert.Equal(5, items.Reports.Count);
        Assert.Equal(FolderCompareStatus.Renamed, items.Reports[^1].Status);
    }

    [Fact]
    public void Streaming_validates_arguments_and_honours_cancellation()
    {
        Assert.Throws<ArgumentNullException>(() => FolderComparer.CompareStreaming(Left, Right, null, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FolderComparer.CompareStreaming(Left, Right, new() { RenameSimilarityThreshold = 0 }, new Collector<FolderCompareProgress>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FolderComparer.CompareStreaming(Left, Right, new() { RenameSimilarityThreshold = 1.5 }, new Collector<FolderCompareProgress>()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FolderComparer.CompareStreaming(Left, Right, null, new Collector<FolderCompareProgress>(), cancellation.Token));
    }

    [Fact]
    public void A_twenty_thousand_file_tree_with_renames_compares_within_the_time_budget()
    {
        const int Folders = 100;
        const int FilesPerFolder = 100;
        Parallel.For(0, Folders, folder =>
        {
            Directory.CreateDirectory(Path.Combine(Left, $"f{folder}"));
            Directory.CreateDirectory(Path.Combine(Right, $"f{folder}"));
            for (var file = 0; file < FilesPerFolder; file++)
            {
                var text = $"file {folder}/{file}\n";
                File.WriteAllText(Path.Combine(Left, $"f{folder}", $"n{file}.txt"), text);

                // Every tenth file is renamed on the right; one in fifty is changed.
                var name = file % 10 == 0 ? $"r{file}.txt" : $"n{file}.txt";
                File.WriteAllText(Path.Combine(Right, $"f{folder}", name), file % 50 == 1 ? text + "changed\n" : text);
            }
        });

        var collector = new Collector<FolderCompareProgress>();
        var stopwatch = Stopwatch.StartNew();
        var result = FolderComparer.CompareStreaming(Left, Right, new() { DetectRenames = true, RenameSimilarityThreshold = 0.9 }, collector);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        Assert.Equal(Folders * FilesPerFolder / 10 * 2, result.Counts[FolderCompareStatus.Renamed]);
        Assert.Equal(Folders * 3, result.Counts[FolderCompareStatus.Different]);
        Assert.Equal(0, result.Counts[FolderCompareStatus.LeftOnly] + result.Counts[FolderCompareStatus.RightOnly]);
        Assert.Equal(result.Items.Count + 1, collector.Reports.Count(r => !r.IsRevision));
    }
}
