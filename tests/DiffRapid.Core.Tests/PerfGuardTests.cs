using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;
using DiffRapid.Core.Patching;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public sealed class PerfGuardTests
{
    [Theory]
    [InlineData(DiffAlgorithmKind.Default)]
    [InlineData(DiffAlgorithmKind.Histogram)]
    [InlineData(DiffAlgorithmKind.Patience)]
    public void Unrelated_lines_round_trip_after_the_search_cutoff(DiffAlgorithmKind algorithm)
    {
        var left = "prefix\n" + string.Concat(Enumerable.Range(0, 1600).Select(index => $"old {index}\n")) + "suffix\n";
        var right = "prefix\n" + string.Concat(Enumerable.Range(0, 1600).Select(index => $"new {index}\n")) + "suffix\n";
        var result = TextComparer.Compare(TextDocument.Parse(left), TextDocument.Parse(right), new CompareOptions { Algorithm = algorithm });

        Assert.Equal(new DiffHunk(1, 1600, 1, 1600), Assert.Single(result.Hunks));
        Assert.Empty(result.MovedBlocks);
        var file = Assert.Single(Patch.Parse(UnifiedDiffWriter.Write(result, "a/file", "b/file")).Files);
        var forward = PatchApplier.Apply(left, file);
        var reverse = PatchApplier.Apply(right, file, new PatchApplyOptions { Reverse = true });
        Assert.True(forward.Succeeded);
        Assert.True(reverse.Succeeded);
        Assert.Equal(right, forward.Text);
        Assert.Equal(left, reverse.Text);
    }

    [Fact]
    public void Bounded_search_keeps_a_valid_script_when_it_discards_an_interior_match()
    {
        var left = Enumerable.Range(0, 1600).ToArray();
        var right = Enumerable.Range(2000, 1600).ToArray();
        right[800] = left[800];
        var bounded = TextComparer.ComputeHunks(left, right, DiffAlgorithmKind.Default, CancellationToken.None);
        var minimal = TextComparer.ComputeHunks(left, right, DiffAlgorithmKind.Minimal, CancellationToken.None);

        AlgorithmTests.AssertValidEditScript(left, right, bounded);
        AlgorithmTests.AssertValidEditScript(left, right, minimal);
        Assert.Equal(new DiffHunk(0, 1600, 0, 1600), Assert.Single(bounded));
        Assert.Equal(2, minimal.Count);
        Assert.Equal(3198, minimal.Sum(hunk => hunk.LeftCount + hunk.RightCount));
    }

    [Fact]
    public void Bounded_search_keeps_thousands_of_scattered_changes_apart()
    {
        // 3,000 changed lines among 60,000: well past a fixed million-step budget, inside one that grows with the input.
        var left = Enumerable.Range(0, 60000).ToArray();
        var right = left.Select(key => key % 20 == 0 ? key + 1000000 : key).ToArray();
        var hunks = TextComparer.ComputeHunks(left, right, DiffAlgorithmKind.Default, CancellationToken.None);

        AlgorithmTests.AssertValidEditScript(left, right, hunks);
        Assert.Equal(3000, hunks.Count);
    }

    [Fact]
    public void Histogram_handles_sparse_keys_without_allocating_by_the_largest_key()
    {
        int[] left = [int.MaxValue, 1, int.MaxValue - 1];
        int[] right = [int.MaxValue - 1, 1, int.MaxValue];
        var hunks = TextComparer.ComputeHunks(left, right, DiffAlgorithmKind.Histogram, CancellationToken.None);

        AlgorithmTests.AssertValidEditScript(left, right, hunks);
    }

    [Fact]
    public void Histogram_balances_equally_good_anchors_to_preserve_scattered_changes()
    {
        var left = string.Concat(Enumerable.Range(0, 1600).Select(index => $"same {index}\nold {index}\n"));
        var right = string.Concat(Enumerable.Range(0, 1600).Select(index => $"same {index}\nnew {index}\n"));
        var result = TextComparer.Compare(TextDocument.Parse(left), TextDocument.Parse(right));

        Assert.Equal(1600, result.Hunks.Count);
        Assert.All(result.Hunks, hunk =>
        {
            Assert.Equal(1, hunk.LeftCount);
            Assert.Equal(1, hunk.RightCount);
        });
    }

    [Fact]
    public void Interned_keys_preserve_content_and_line_ending_identity()
    {
        var document = TextDocument.Parse("a\na\r\na\ra");
        var normalizer = new LineNormalizer(CompareOptions.Default);
        var keys = normalizer.ToKeys(document);

        Assert.Equal(4, keys.Distinct().Count());
        Assert.Equal(keys, normalizer.ToKeys(document));
        var ignoreEndings = new LineNormalizer(new CompareOptions { IgnoreLineEndings = true });
        Assert.Single(ignoreEndings.ToKeys(document).Distinct());
        var controlCharacters = new LineNormalizer(CompareOptions.Default);
        Assert.NotEqual(controlCharacters.ToKeys(TextDocument.Parse("a\u0001lf"))[0], controlCharacters.ToKeys(TextDocument.Parse("a\n"))[0]);
    }

    [Fact]
    public void Text_key_generation_and_inline_comparison_observe_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var document = TextDocument.Parse("same\n");

        Assert.Throws<OperationCanceledException>(() => new LineNormalizer(CompareOptions.Default).ToKeys(document, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => TextComparer.Compare(document, document, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => InlineDiffer.Compare("same", "same", null, cancellation.Token));
    }

    [Theory]
    [InlineData(InlineGranularity.Word)]
    [InlineData(InlineGranularity.Character)]
    public void Huge_inline_inputs_use_whole_line_spans(InlineGranularity granularity)
    {
        var left = new string('a', InlineDiffer.MaxInputLength + 1);
        var right = new string('b', InlineDiffer.MaxInputLength + 1);
        var options = new CompareOptions { InlineGranularity = granularity };
        var difference = InlineDiffer.Compare(left, right, options);

        Assert.Equal(new TextSpan(0, left.Length), Assert.Single(difference.Left));
        Assert.Equal(new TextSpan(0, right.Length), Assert.Single(difference.Right));
        Assert.Empty(InlineDiffer.Compare(left, left, options).Left);
        Assert.Empty(InlineDiffer.Compare(left, string.Empty, options).Right);
    }

    [Fact]
    public void Huge_hunks_are_reported_without_inline_work()
    {
        var count = SideBySideHtml.MaxInlineHunkLines + 1;
        var left = TextDocument.Parse(string.Concat(Enumerable.Repeat("old words\n", count)));
        var right = TextDocument.Parse(string.Concat(Enumerable.Repeat("new words\n", count)));
        var result = TextComparer.Compare(left, right);
        using var writer = new StringWriter();
        FileReportWriter.WriteHtml(result, new FileReportOptions(), writer);

        var html = writer.ToString();
        Assert.DoesNotContain("<mark>", html);
        Assert.Equal(count, html.Split("old words", StringSplitOptions.None).Length - 1);
        Assert.Equal(count, html.Split("new words", StringSplitOptions.None).Length - 1);
        Assert.EndsWith("</body></html>", html);
    }

    [Fact]
    public void Report_cancellation_is_checked_between_rows_of_one_hunk()
    {
        var result = TextComparer.Compare(TextDocument.Parse("old one\nold two\n"), TextDocument.Parse("new one\nnew two\n"));
        using var cancellation = new CancellationTokenSource();
        using var writer = new CancelAfterRowWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() => FileReportWriter.WriteHtml(result, new FileReportOptions(), writer, cancellation.Token));
        Assert.DoesNotContain("two", writer.ToString());
    }

    [Fact]
    public void Folder_report_observes_cancellation_between_streamed_rows()
    {
        var metadata = new FileMeta(1, DateTime.UtcNow, FileAttributes.Normal, true);
        var files = new[] { "first.txt", "second.txt" }
            .Select(path => new FolderCompareItem(path, FolderItemKind.File, FolderCompareStatus.Different, metadata, metadata, null, null, []))
            .ToArray();
        var root = new FolderCompareItem("", FolderItemKind.Folder, FolderCompareStatus.Different, metadata, metadata, null, null, files);
        var result = new FolderCompareResult(root, files, new Dictionary<FolderCompareStatus, int>());
        using var cancellation = new CancellationTokenSource();
        using var writer = new CancelOnFileWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() => FolderReportWriter.WriteHtml(result, new FolderReportOptions(), writer, cancellation.Token));
        Assert.Contains("first.txt", writer.ToString());
        Assert.DoesNotContain("second.txt", writer.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Reused_folder_buffers_handle_different_file_lengths_and_parallel_workers(int parallelism)
    {
        var root = Path.Combine(Path.GetTempPath(), "diffrapid-perf-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteBufferTestFiles(left, right, timestamp);

            foreach (var method in new[] { FolderCompareMethod.BinaryContents, FolderCompareMethod.DateAndSize })
            {
                var result = FolderComparer.Compare(left, right, new FolderCompareOptions { Method = method, MaxDegreeOfParallelism = parallelism });
                Assert.Equal(24, result.Items.Count);
                Assert.Equal(0, result.Counts[FolderCompareStatus.Error]);
                Assert.Equal(12, result.Items.Count(item => item.Kind == FolderItemKind.File && item.Status == FolderCompareStatus.Different));
                Assert.Equal(8, result.Items.Count(item => item.Kind == FolderItemKind.File && item.Status == FolderCompareStatus.Identical));
                Assert.All(result.Items.Where(item => item.Kind == FolderItemKind.File), item => Assert.Equal(timestamp, item.Left.LastWriteUtc));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void WriteBufferTestFiles(string left, string right, DateTime timestamp)
    {
        var random = new Random(25);
        var lengths = new[] { 1, 65535, 65536, 65537, 131073 };
        for (var folder = 0; folder < 4; folder++)
        {
            Directory.CreateDirectory(Path.Combine(left, folder.ToString()));
            Directory.CreateDirectory(Path.Combine(right, folder.ToString()));
            for (var index = 0; index < lengths.Length; index++)
            {
                var bytes = new byte[lengths[index]];
                random.NextBytes(bytes);
                var leftPath = Path.Combine(left, folder.ToString(), index.ToString());
                var rightPath = Path.Combine(right, folder.ToString(), index.ToString());
                File.WriteAllBytes(leftPath, bytes);
                if (index % 2 == 0)
                {
                    bytes[bytes.Length - 1] ^= 127;
                }

                File.WriteAllBytes(rightPath, bytes);
                File.SetLastWriteTimeUtc(leftPath, timestamp);
                File.SetLastWriteTimeUtc(rightPath, index % 2 == 0 ? timestamp.AddSeconds(5) : timestamp);
            }
        }
    }

    private sealed class CancelAfterRowWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            if (value == "</tr>")
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class CancelOnFileWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            if (value?.Contains("first.txt", StringComparison.Ordinal) == true)
            {
                cancellation.Cancel();
            }
        }
    }
}
