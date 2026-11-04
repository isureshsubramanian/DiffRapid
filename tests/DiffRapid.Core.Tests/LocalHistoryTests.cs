using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiffRapid.Cli;
using DiffRapid.Core.History;

namespace DiffRapid.Core.Tests;

public sealed class LocalHistoryTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-history-" + Guid.NewGuid());
    private string StoreFolder => Path.Combine(_root, "history store");
    private string SourcePath => Path.Combine(_root, "source.txt");

    public LocalHistoryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DefaultsMatchTheCaptureAndRetentionLimits()
    {
        var options = new LocalHistoryOptions();
        Assert.Equal(5 * 1024 * 1024, options.MaxFileSize);
        Assert.False(options.IncludeBinary);
        Assert.Equal(TimeSpan.FromDays(30), options.MaxAge);
        Assert.Equal(100, options.MaxEntriesPerFile);
        Assert.Equal(500 * 1024 * 1024, options.MaxTotalSize);
    }

    [Fact]
    public void SnapshotStoresMetadataAndGzipContentUnderItsSha256()
    {
        var store = OpenStore();
        byte[] bytes = Encoding.UTF8.GetBytes("saved content\r\n");
        File.WriteAllBytes(SourcePath, bytes);
        var before = DateTimeOffset.UtcNow;
        var entry = Assert.IsType<LocalHistoryEntry>(store.Snapshot(SourcePath, "Before merge", "keep this version"));
        Assert.InRange(entry.TimestampUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, entry.TimestampUtc.Offset);
        Assert.Equal("Before merge", entry.Label);
        Assert.Equal("keep this version", entry.Note);
        Assert.Equal(Path.GetFullPath(SourcePath), entry.OriginalPath);
        Assert.Equal(bytes.Length, entry.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Hash);
        Assert.Equal(bytes, store.Read(entry));
        using var blob = File.OpenRead(BlobPath(entry));
        using var gzip = new GZipStream(blob, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        gzip.CopyTo(decoded);
        Assert.Equal(bytes, decoded.ToArray());
        var index = Assert.Single(IndexFiles());
        Assert.Equal(64, Path.GetFileNameWithoutExtension(index).Length);
        Assert.Single(File.ReadAllLines(index));
    }

    [Fact]
    public void DedupSkipsOnlyTheLatestContentAndSharesBlobsAcrossPaths()
    {
        var store = OpenStore();
        var first = Capture(store, "first");
        Assert.Null(store.SnapshotText(SourcePath, "first", new UTF8Encoding(false), "Before patch"));
        var second = Capture(store, "second");
        var repeated = Capture(store, "first");
        var other = Capture(store, "first", Path.Combine(_root, "other.txt"));
        Assert.Equal(first.Hash, repeated.Hash);
        Assert.Equal(first.Hash, other.Hash);
        Assert.Equal(new[] { repeated, second, first }, store.List(SourcePath));
        Assert.Equal(2, BlobFiles().Length);
        Assert.Equal(3, File.ReadLines(IndexFor(SourcePath)).Count());
    }

    [Fact]
    public void FileAndBufferSnapshotsOfTheSameBytesDeduplicate()
    {
        var store = OpenStore();
        File.WriteAllText(SourcePath, "same\n", new UTF8Encoding(false));
        Assert.NotNull(store.Snapshot(SourcePath, "Saved"));
        Assert.Null(store.SnapshotText(SourcePath, "same\n", new UTF8Encoding(false), "Saved"));
        Assert.Single(store.List(SourcePath));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void FileSizeLimitIncludesItsBoundary(int size, bool skipped)
    {
        var store = OpenStore(new LocalHistoryOptions { MaxFileSize = 4, IncludeBinary = true });
        File.WriteAllBytes(SourcePath, Enumerable.Repeat((byte)1, size).ToArray());
        Assert.Equal(skipped, store.Snapshot(SourcePath, "Saved") is null);
        Assert.Equal(skipped ? 0 : 1, BlobFiles().Length);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7999, true)]
    [InlineData(8000, false)]
    public void BinaryProbeChecksExactlyTheFirst8000Bytes(int nulOffset, bool skipped)
    {
        var store = OpenStore();
        var bytes = Enumerable.Repeat((byte)1, 8100).ToArray();
        bytes[nulOffset] = 0;
        File.WriteAllBytes(SourcePath, bytes);
        Assert.Equal(skipped, store.Snapshot(SourcePath, "Saved") is null);
        Assert.Equal(skipped ? 0 : 1, BlobFiles().Length);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void IncludeBinaryAllowsNulButKeepsTheSizeLimit()
    {
        var store = OpenStore(new LocalHistoryOptions { IncludeBinary = true, MaxFileSize = 3 });
        File.WriteAllBytes(SourcePath, [0, 1, 2]);
        var entry = Assert.IsType<LocalHistoryEntry>(store.Snapshot(SourcePath, "Before patch"));
        Assert.Equal(new byte[] { 0, 1, 2 }, store.Read(entry));
        File.WriteAllBytes(SourcePath, [0, 1, 2, 3]);
        Assert.Null(store.Snapshot(SourcePath, "Saved"));
        Assert.Single(store.List(SourcePath));
    }

    [Fact]
    public void EmptyFilesAreCapturedAtAZeroSizeLimit()
    {
        var store = OpenStore(new LocalHistoryOptions { MaxFileSize = 0 });
        File.WriteAllBytes(SourcePath, []);
        var entry = Assert.IsType<LocalHistoryEntry>(store.Snapshot(SourcePath, "Saved"));
        Assert.Empty(store.Read(entry));
        Assert.Equal(0, entry.Size);
        Assert.Null(store.Snapshot(SourcePath, "Saved"));
    }

    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(1200, true)]
    [InlineData(1201, true)]
    [InlineData(12000, true)]
    [InlineData(28591, false)]
    public void BufferSnapshotsPreserveEncodingPreambleAndSplitSurrogates(int codePage, bool preamble)
    {
        var store = OpenStore(new LocalHistoryOptions { IncludeBinary = true });
        var encoding = codePage == 65001 ? new UTF8Encoding(preamble) : Encoding.GetEncoding(codePage);
        var text = new string('x', 4095) + "😀\r\nété\n";
        var entry = Assert.IsType<LocalHistoryEntry>(store.SnapshotText(SourcePath, text, encoding, "Saved"));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(), store.Read(entry));
        Assert.False(File.Exists(SourcePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void BufferSnapshotsApplySizeAndBinaryExclusionsWithoutChangingTheFile()
    {
        var store = OpenStore(new LocalHistoryOptions { MaxFileSize = 3 });
        File.WriteAllText(SourcePath, "disk content");
        Assert.Null(store.SnapshotText(SourcePath, "a", new UTF8Encoding(true), "Saved"));
        Assert.Null(store.SnapshotText(SourcePath, "a\0", new UTF8Encoding(false), "Saved"));
        Assert.Null(store.SnapshotText(SourcePath, new string('x', 200000), new UTF8Encoding(false), "Saved"));
        Assert.Empty(store.List(SourcePath));
        Assert.Empty(BlobFiles());
        Assert.Equal("disk content", File.ReadAllText(SourcePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void RelativeSegmentsAndWindowsPathCaseUseOneIndex()
    {
        var store = OpenStore();
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var entry = Capture(store, "saved", Path.Combine(_root, "nested", "..", "source.txt"));
        Assert.Equal(SourcePath, entry.OriginalPath);
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(entry, Assert.Single(store.List(SourcePath.ToUpperInvariant())));
            Assert.Null(store.SnapshotText(SourcePath.ToUpperInvariant(), "saved", new UTF8Encoding(false), "Saved"));
            store.Rename(SourcePath, SourcePath.ToUpperInvariant());
            Assert.Single(store.List(SourcePath));
        }
        else
        {
            Assert.Empty(store.List(SourcePath.ToUpperInvariant()));
        }

        Assert.Single(IndexFiles());
    }

    [Fact]
    public void MaxAgeRemovesOnlyOldSnapshotsAndTheirUnreferencedBlobs()
    {
        var store = OpenStore();
        var old = Capture(store, "old") with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-31) };
        var recent = Capture(store, "recent") with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-29) };
        RewriteIndex(SourcePath, old, recent);
        store.Prune();
        Assert.Equal(recent, Assert.Single(store.List(SourcePath)));
        Assert.False(File.Exists(BlobPath(old)));
        Assert.True(File.Exists(BlobPath(recent)));
    }

    [Fact]
    public void MaxEntriesRetainsTheNewestVersionsAndPruningIsIdempotent()
    {
        var store = OpenStore(new LocalHistoryOptions { MaxEntriesPerFile = 2 });
        var oldest = Capture(store, "oldest");
        var middle = Capture(store, "middle");
        var newest = Capture(store, "newest");
        store.Prune();
        Assert.Equal(new[] { newest, middle }, store.List(SourcePath));
        Assert.False(File.Exists(BlobPath(oldest)));
        store.Prune();
        Assert.Equal(new[] { newest, middle }, store.List(SourcePath));
        Assert.Equal(2, BlobFiles().Length);
    }

    [Fact]
    public void EqualTimestampsUseCaptureOrderAcrossPrunes()
    {
        var store = OpenStore();
        var timestamp = DateTimeOffset.UtcNow;
        var first = Capture(store, "first") with { TimestampUtc = timestamp };
        var second = Capture(store, "second") with { TimestampUtc = timestamp };
        RewriteIndex(SourcePath, first, second);
        Assert.Equal(new[] { second, first }, store.List(SourcePath));
        store.Prune();
        store.Prune();
        Assert.Equal(new[] { second, first }, store.List(SourcePath));
        Assert.Null(store.SnapshotText(SourcePath, "second", new UTF8Encoding(false), "Saved"));
    }

    [Theory]
    [InlineData("age")]
    [InlineData("entries")]
    [InlineData("size")]
    public void ZeroRetentionLimitsRemoveAllSnapshots(string limit)
    {
        var options = limit switch
        {
            "age" => new LocalHistoryOptions { MaxAge = TimeSpan.Zero },
            "entries" => new LocalHistoryOptions { MaxEntriesPerFile = 0 },
            _ => new LocalHistoryOptions { MaxTotalSize = 0 },
        };
        var store = OpenStore(options);
        Capture(store, "content");
        store.Prune();
        Assert.Empty(store.List(SourcePath));
        Assert.Empty(BlobFiles());
        Assert.Empty(IndexFiles());
    }

    [Fact]
    public void TotalSizeEvictsOldestAcrossFilesUsingCompressedBlobSizes()
    {
        var store = OpenStore();
        var otherPath = Path.Combine(_root, "other.txt");
        var old = Capture(store, new string('a', 10000));
        var middle = Capture(store, new string('b', 10000), otherPath);
        var newest = Capture(store, new string('c', 10000));
        var budget = new FileInfo(BlobPath(middle)).Length + new FileInfo(BlobPath(newest)).Length;
        OpenStore(new LocalHistoryOptions { MaxTotalSize = budget }).Prune();
        Assert.Equal(newest, Assert.Single(store.List(SourcePath)));
        Assert.Equal(middle, Assert.Single(store.List(otherPath)));
        Assert.False(File.Exists(BlobPath(old)));
        Assert.Equal(budget, BlobFiles().Sum(path => new FileInfo(path).Length));
    }

    [Fact]
    public void TotalSizeCountsASharedBlobOnceAndRemovesItOnlyAfterItsLastReference()
    {
        var store = OpenStore();
        var first = Capture(store, "shared content");
        var otherPath = Path.Combine(_root, "other.txt");
        Capture(store, "shared content", otherPath);
        var blobSize = new FileInfo(BlobPath(first)).Length;
        OpenStore(new LocalHistoryOptions { MaxTotalSize = blobSize }).Prune();
        Assert.Single(store.List(SourcePath));
        Assert.Single(store.List(otherPath));
        Assert.Single(BlobFiles());
        OpenStore(new LocalHistoryOptions { MaxTotalSize = blobSize - 1 }).Prune();
        Assert.Empty(store.List(SourcePath));
        Assert.Empty(store.List(otherPath));
        Assert.Empty(BlobFiles());
    }

    [Fact]
    public void TotalSizeEvictsTheEarlierCaptureWhenTimestampsMatch()
    {
        var store = OpenStore();
        var timestamp = DateTimeOffset.UtcNow;
        var first = Capture(store, "first") with { TimestampUtc = timestamp };
        var second = Capture(store, "second") with { TimestampUtc = timestamp };
        RewriteIndex(SourcePath, first, second);
        OpenStore(new LocalHistoryOptions { MaxTotalSize = new FileInfo(BlobPath(second)).Length }).Prune();
        Assert.Equal(second, Assert.Single(store.List(SourcePath)));
        Assert.False(File.Exists(BlobPath(first)));
    }

    [Fact]
    public void PerFilePruningKeepsBlobsReferencedByAnotherFile()
    {
        var store = OpenStore(new LocalHistoryOptions { MaxEntriesPerFile = 1 });
        var shared = Capture(store, "shared");
        var otherPath = Path.Combine(_root, "other.txt");
        Capture(store, "shared", otherPath);
        Capture(store, "new");
        store.Prune();
        Assert.Equal(Encoding.UTF8.GetBytes("shared"), store.Read(Assert.Single(store.List(otherPath))));
        Assert.True(File.Exists(BlobPath(shared)));
        Assert.Equal(2, BlobFiles().Length);
    }

    [Fact]
    public void PruneRemovesOrphansAndAbandonedTemporaryFiles()
    {
        var store = OpenStore();
        var entry = Capture(store, "keep");
        var orphanFolder = Path.Combine(StoreFolder, "blobs", "ff");
        Directory.CreateDirectory(orphanFolder);
        var orphan = Path.Combine(orphanFolder, new string('f', 64));
        File.WriteAllText(orphan, "unreferenced");
        File.WriteAllText(Path.Combine(StoreFolder, "blobs", ".abandoned.tmp"), "partial compression");
        File.WriteAllText(Path.Combine(StoreFolder, "index", ".abandoned.tmp"), "partial index");
        store.Prune();
        Assert.False(File.Exists(orphan));
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
        Assert.Single(BlobFiles());
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void RenameMovesHistoryAndPreservesTheOriginalCapturePath()
    {
        var store = OpenStore();
        var entry = Capture(store, "before rename");
        var newPath = Path.Combine(_root, "renamed.txt");
        store.Rename(SourcePath, newPath);
        Assert.Empty(store.List(SourcePath));
        Assert.Equal(entry, Assert.Single(store.List(newPath)));
        Assert.Equal(SourcePath, entry.OriginalPath);
        Assert.Single(IndexFiles());
        Assert.Null(store.SnapshotText(newPath, "before rename", new UTF8Encoding(false), "Saved"));
        var after = Capture(store, "after rename", newPath);
        Assert.Equal(newPath, after.OriginalPath);
        store.Prune();
        Assert.Equal(2, store.List(newPath).Count);
        Assert.Equal(Encoding.UTF8.GetBytes("before rename"), store.Read(entry));
    }

    [Fact]
    public void RenameMergesExistingHistoryWithoutLosingEitherFilesSnapshots()
    {
        var store = OpenStore();
        var old = Capture(store, "old path");
        var newPath = Path.Combine(_root, "renamed.txt");
        var destination = Capture(store, "destination", newPath);
        store.Rename(SourcePath, newPath);
        Assert.Equal(new[] { destination, old }, store.List(newPath));
        Assert.Empty(store.List(SourcePath));
        store.Rename(SourcePath, newPath);
        store.Rename(newPath, newPath);
        Assert.Equal(2, store.List(newPath).Count);
        Assert.Single(IndexFiles());
    }

    [Fact]
    public void RestoreAtomicallyReplacesAndCreatesFilesWithExactBytes()
    {
        var store = OpenStore(new LocalHistoryOptions { IncludeBinary = true });
        byte[] bytes = [0, 0xff, 0x80, 10, 13];
        File.WriteAllBytes(SourcePath, bytes);
        var entry = Assert.IsType<LocalHistoryEntry>(store.Snapshot(SourcePath, "Saved"));
        File.WriteAllText(SourcePath, "changed");
        store.Restore(entry, SourcePath);
        Assert.Equal(bytes, File.ReadAllBytes(SourcePath));
        var absentPath = Path.Combine(_root, "restored.txt");
        store.Restore(entry, absentPath);
        Assert.Equal(bytes, File.ReadAllBytes(absentPath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ReadersSeeOnlyCompleteVersionsDuringRestore()
    {
        var store = OpenStore();
        var first = Capture(store, new string('a', 128 * 1024));
        var second = Capture(store, new string('b', 128 * 1024));
        store.Restore(first, SourcePath);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observer = Task.Run(() => ObserveRestores(started, stop.Token));
        try
        {
            await started.Task.WaitAsync(stop.Token);
            for (var index = 0; index < 12; index++)
            {
                store.Restore(index % 2 == 0 ? second : first, SourcePath, stop.Token);
            }
        }
        finally
        {
            stop.Cancel();
            await observer;
        }

        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("gzip")]
    public void CorruptBlobsDoNotChangeTheRestoreTarget(string corruption)
    {
        var store = OpenStore();
        var entry = Capture(store, "saved content");
        File.WriteAllText(SourcePath, "keep target");
        if (corruption == "size")
        {
            entry = entry with { Size = entry.Size - 1 };
        }
        else if (corruption == "hash")
        {
            using var output = File.Create(BlobPath(entry));
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            gzip.Write(Encoding.UTF8.GetBytes("wrong content"));
        }
        else
        {
            File.WriteAllBytes(BlobPath(entry), [1, 2, 3, 4]);
        }

        Assert.Throws<InvalidDataException>(() => store.Read(entry));
        Assert.Throws<InvalidDataException>(() => store.Restore(entry, SourcePath));
        Assert.Equal("keep target", File.ReadAllText(SourcePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void LockedRestoreTargetRemainsIntactOnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = OpenStore();
        var entry = Capture(store, "snapshot");
        File.WriteAllText(SourcePath, "keep target");
        using (var lockedTarget = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Restore(entry, SourcePath));
        }

        Assert.Equal("keep target", File.ReadAllText(SourcePath));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task TwoStoreInstancesSerializeConcurrentSnapshotsWithoutLostEntries()
    {
        var first = OpenStore();
        var second = OpenStore();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var store = worker % 2 == 0 ? first : second;
            for (var index = 0; index < 5; index++)
            {
                Capture(store, $"worker {worker}, version {index}");
            }
        }));
        await Task.WhenAll(tasks);
        var entries = first.List(SourcePath);
        Assert.Equal(40, entries.Count);
        Assert.Equal(40, entries.Select(entry => entry.Hash).Distinct().Count());
        Assert.Equal(40, File.ReadLines(IndexFor(SourcePath)).Count());
        Assert.All(entries, entry => Assert.Equal(entry.Size, second.Read(entry).Length));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task TwoInstancesDeduplicateSimultaneousIdenticalSnapshots()
    {
        var first = OpenStore();
        var second = OpenStore();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SnapshotText(SourcePath, "same", new UTF8Encoding(false), "Saved"))));
        Assert.Single(first.List(SourcePath));
        Assert.Single(BlobFiles());
    }

    [Fact]
    public async Task PruneAndSnapshotsFromSeparateInstancesKeepAllReferencedBlobs()
    {
        var writer = OpenStore();
        var pruner = OpenStore();
        var snapshots = Task.Run(() =>
        {
            for (var index = 0; index < 12; index++)
            {
                Capture(writer, $"version {index}");
            }
        });
        var pruning = Task.Run(() =>
        {
            for (var index = 0; index < 12; index++)
            {
                pruner.Prune();
            }
        });
        await Task.WhenAll(snapshots, pruning);
        Assert.Equal(12, writer.List(SourcePath).Count);
        Assert.All(writer.List(SourcePath), entry => Assert.Equal(entry.Size, writer.Read(entry).Length));
    }

    [Fact]
    public void CorruptAndTruncatedIndexLinesAreSkippedAndNewSnapshotsRemainReadable()
    {
        var store = OpenStore();
        var entry = Capture(store, "first");
        var indexPath = IndexFor(SourcePath);
        var validLine = File.ReadAllText(indexPath);
        File.WriteAllText(indexPath, "not json\n{}\nnull\n" + validLine + "{\"hash\":");
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
        var newest = Capture(store, "second");
        Assert.Equal(new[] { newest, entry }, store.List(SourcePath));
        store.Prune();
        Assert.Equal(2, File.ReadAllLines(indexPath).Length);
        Assert.Equal(new[] { newest, entry }, store.List(SourcePath));
        Assert.Equal(Encoding.UTF8.GetBytes("first"), store.Read(entry));
    }

    [Fact]
    public void InvalidHashesCannotEscapeTheBlobFolder()
    {
        var store = OpenStore();
        var entry = Capture(store, "safe");
        var invalid = entry with { Hash = "../../outside" };
        Assert.Throws<ArgumentException>(() => store.Read(invalid));
        Assert.Throws<ArgumentException>(() => store.Restore(invalid, SourcePath));
        File.AppendAllText(IndexFor(SourcePath), JsonSerializer.Serialize(invalid, JsonOptions) + "\n");
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
    }

    [Fact]
    public void CancellationLeavesHistoryAndRestoreTargetUntouched()
    {
        var store = OpenStore();
        var entry = Capture(store, "saved");
        File.WriteAllText(SourcePath, "keep target");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Snapshot(SourcePath, "Saved", cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.SnapshotText(SourcePath, "new", Encoding.UTF8, "Saved", cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.List(SourcePath, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.Read(entry, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.Restore(entry, SourcePath, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.Prune(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.Rename(SourcePath, SourcePath + ".new", cancellation.Token));
        Assert.Equal("keep target", File.ReadAllText(SourcePath));
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void WaitingForAnotherProcessLockCanBeCanceled()
    {
        var store = OpenStore();
        using var heldLock = new FileStream(Path.Combine(StoreFolder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var cancelThread = new Thread(() =>
        {
            Thread.Sleep(100);
            cancellation.Cancel();
        });
        cancelThread.Start();
        try
        {
            Assert.Throws<OperationCanceledException>(() => store.List(SourcePath, cancellation.Token));
        }
        finally
        {
            cancelThread.Join();
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("age")]
    [InlineData("entries")]
    [InlineData("total")]
    public void NegativeLimitsAreRejected(string limit)
    {
        var options = limit switch
        {
            "file" => new LocalHistoryOptions { MaxFileSize = -1 },
            "age" => new LocalHistoryOptions { MaxAge = TimeSpan.FromTicks(-1) },
            "entries" => new LocalHistoryOptions { MaxEntriesPerFile = -1 },
            _ => new LocalHistoryOptions { MaxTotalSize = -1 },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenStore(options));
    }

    [Fact]
    public void AnEmptyStoreAndAnUnlimitedAgeCanBePruned()
    {
        var store = OpenStore(new LocalHistoryOptions { MaxAge = TimeSpan.MaxValue });
        Assert.Empty(store.List(SourcePath));
        store.Prune();
        store.Rename(SourcePath, SourcePath + ".new");
        var entry = Capture(store, "saved");
        store.Prune();
        Assert.Equal(entry, Assert.Single(store.List(SourcePath)));
    }

    [Fact]
    public async Task CliListsShowsAndComparesSnapshotsAndTheCurrentFile()
    {
        File.WriteAllText(SourcePath, "first\n");
        var snapshot = await RunCli("history", "snapshot", SourcePath, "--label", "Before patch", "--store", StoreFolder);
        Assert.Equal(0, snapshot.ExitCode);
        var store = OpenStore();
        Assert.Equal("Before patch", Assert.Single(store.List(SourcePath)).Label);
        File.WriteAllText(SourcePath, "second\n");
        Assert.Equal(0, (await RunCli("history", "snapshot", SourcePath, "--store=" + StoreFolder)).ExitCode);
        var listed = await RunCli("history", "--store", StoreFolder, "list", SourcePath);
        Assert.Equal(0, listed.ExitCode);
        var lines = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1 ", lines[0]);
        Assert.Contains("Saved", lines[0]);
        Assert.StartsWith("2 ", lines[1]);
        Assert.Contains("Before patch", lines[1]);
        var shown = await RunCli("history", "show", SourcePath, "2", "--store", StoreFolder);
        Assert.Equal(0, shown.ExitCode);
        Assert.Equal("first\n", shown.Output);
        var diff = await RunCli("history", "diff", SourcePath, "2", "1", "--store", StoreFolder);
        Assert.Equal(1, diff.ExitCode);
        Assert.Contains("-first", diff.Output);
        Assert.Contains("+second", diff.Output);
        Assert.Contains(SourcePath + "@2", diff.Output);
        Assert.Contains(SourcePath + "@1", diff.Output);
        var same = await RunCli("history", "diff", SourcePath, "1", "--store", StoreFolder);
        Assert.Equal(0, same.ExitCode);
        File.WriteAllText(SourcePath, "current\n");
        var current = await RunCli("history", "diff", SourcePath, "1", "--store", StoreFolder);
        Assert.Equal(1, current.ExitCode);
        Assert.Contains("-second", current.Output);
        Assert.Contains("+current", current.Output);
        Assert.Equal(0, (await RunCli("history", "prune", "--store", StoreFolder)).ExitCode);
        Assert.Equal(2, store.List(SourcePath).Count);
    }

    [Fact]
    public async Task CliShowWritesExactBinaryBytesAndTextDiffRejectsThem()
    {
        var store = OpenStore(new LocalHistoryOptions { IncludeBinary = true });
        byte[] bytes = [0, 1, 0xff, 0x80, 13, 10];
        File.WriteAllBytes(SourcePath, bytes);
        Assert.NotNull(store.Snapshot(SourcePath, "Saved"));
        var shown = await RunCli("history", "show", SourcePath, "1", "--store", StoreFolder);
        Assert.Equal(0, shown.ExitCode);
        Assert.Equal(bytes, shown.Bytes);
        var diff = await RunCli("history", "diff", SourcePath, "1", "--store", StoreFolder);
        Assert.Equal(2, diff.ExitCode);
        Assert.Contains("binary", diff.Error);
    }

    [Fact]
    public async Task CliHistoryDiffMatchesNormalTextCompareIncludingOptionsAndLineEndings()
    {
        var store = OpenStore();
        Capture(store, "été\nold\n");
        File.WriteAllText(SourcePath, "ÉTÉ\nnew\n");
        var previousPath = Path.Combine(_root, "previous.txt");
        File.WriteAllText(previousPath, "été\nold\n");
        string[] options = ["--ignore-case", "--context", "0", "--left-label", "before", "--right-label", "after"];
        var history = await RunCli(["history", "diff", SourcePath, "1", "--store", StoreFolder, .. options]);
        var normal = await RunCli(["diff", previousPath, SourcePath, .. options]);
        Assert.Equal(1, history.ExitCode);
        Assert.Equal(normal.ExitCode, history.ExitCode);
        Assert.Equal(normal.Bytes, history.Bytes);
        Assert.DoesNotContain('\r', history.Output);
        var quiet = await RunCli("history", "diff", SourcePath, "1", "--store", StoreFolder, "--quiet");
        Assert.Equal(1, quiet.ExitCode);
        Assert.Empty(quiet.Bytes);
    }

    [Fact]
    public void CliReportsCorruptBlobsAsAnError()
    {
        var entry = Capture(OpenStore(), "saved");
        File.WriteAllBytes(BlobPath(entry), [1, 2, 3, 4]);
        Assert.Equal(2, Program.Main(["history", "show", SourcePath, "1", "--store", StoreFolder]));
    }

    [Fact]
    public async Task CliSnapshotsFromIndependentProcessesShareOneStoreSafely()
    {
        File.WriteAllText(SourcePath, "same content\n");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            RunCli("history", "snapshot", SourcePath, "--store", StoreFolder)));
        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Single(OpenStore().List(SourcePath));
        Assert.Single(BlobFiles());
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CliPruneRemovesExpiredVersionsAndTheirBlobs()
    {
        var store = OpenStore();
        var entry = Capture(store, "expired") with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-31) };
        RewriteIndex(SourcePath, entry);
        Assert.Equal(0, (await RunCli("history", "prune", "--store", StoreFolder)).ExitCode);
        Assert.Empty(store.List(SourcePath));
        Assert.Empty(BlobFiles());
    }

    [Theory]
    [InlineData("show", "0")]
    [InlineData("show", "2")]
    [InlineData("show", "invalid")]
    [InlineData("diff", "999999999999999")]
    public void CliRejectsInvalidSnapshotNumbers(string action, string number)
    {
        Capture(OpenStore(), "saved");
        Assert.Equal(2, Program.Main(["history", action, SourcePath, number, "--store", StoreFolder]));
    }

    [Theory]
    [InlineData("missing-action")]
    [InlineData("list")]
    [InlineData("show")]
    [InlineData("diff")]
    [InlineData("snapshot")]
    public void CliRejectsUnknownOrIncompleteActions(string action)
    {
        Assert.Equal(2, Program.Main(["history", action, "--store", StoreFolder]));
    }

    [Fact]
    public void CliRejectsMissingOptionValuesAndTooManyArguments()
    {
        Assert.Equal(2, Program.Main(["history"]));
        Assert.Equal(2, Program.Main(["history", "prune", "extra", "--store", StoreFolder]));
        Assert.Equal(2, Program.Main(["history", "list", SourcePath, "--store"]));
        Assert.Equal(2, Program.Main(["history", "snapshot", SourcePath, "--label", "--store", StoreFolder]));
        Assert.Equal(2, Program.Main(["history", "snapshot", SourcePath, "--label=", "--store", StoreFolder]));
        Assert.Equal(2, Program.Main(["history", "list", SourcePath, "--store="]));
    }

    private LocalHistoryStore OpenStore(LocalHistoryOptions? options = null) => new(StoreFolder, options ?? new LocalHistoryOptions());

    private LocalHistoryEntry Capture(LocalHistoryStore store, string text, string? path = null) =>
        Assert.IsType<LocalHistoryEntry>(store.SnapshotText(path ?? SourcePath, text, new UTF8Encoding(false), "Saved"));

    private string BlobPath(LocalHistoryEntry entry) => Path.Combine(StoreFolder, "blobs", entry.Hash[..2], entry.Hash);

    private string[] BlobFiles() => Directory.GetFiles(Path.Combine(StoreFolder, "blobs"), "*", SearchOption.AllDirectories);

    private string[] IndexFiles() => Directory.GetFiles(Path.Combine(StoreFolder, "index"), "*.jsonl");

    private string IndexFor(string path) => IndexFiles().Single(index => File.ReadLines(index).Any(line =>
        JsonSerializer.Deserialize<LocalHistoryEntry>(line, JsonOptions)?.OriginalPath == path));

    private void RewriteIndex(string path, params LocalHistoryEntry[] entries) =>
        File.WriteAllLines(IndexFor(path), entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));

    private void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));

    private void ObserveRestores(TaskCompletionSource started, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = ReadDuringRestore();
                if (bytes is null)
                {
                    continue;
                }

                Assert.Equal(128 * 1024, bytes.Length);
                var expected = bytes[0];
                Assert.True(expected is (byte)'a' or (byte)'b');
                Assert.True(bytes.All(value => value == expected));
                started.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            throw;
        }
    }

    private byte[]? ReadDuringRestore()
    {
        try
        {
            using var input = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream();
            input.CopyTo(contents);
            return contents.ToArray();
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) is 32 or 33)
        {
            // Windows can briefly deny a new handle while ReplaceFile exchanges the files.
            return null;
        }
    }

    private static async Task<CliResult> RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var output = new MemoryStream();
        try
        {
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var readError = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(readOutput, readError, process.WaitForExitAsync(timeout.Token));
            return new CliResult(process.ExitCode, output.ToArray(), await readError);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed record CliResult(int ExitCode, byte[] Bytes, string Error)
    {
        public string Output => Encoding.UTF8.GetString(Bytes);
    }
}
