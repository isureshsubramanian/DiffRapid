using System.IO.Compression;
using DiffRapid.Core.Assemblies;
using DiffRapid.Core.Plugins;

namespace DiffRapid.Core.Tests;

/// <summary>Zip readers stay within their entry-count, per-entry and total budgets.</summary>
public sealed class ZipLimitsTests
{
    [Fact]
    public void BudgetDropsAnOversizedEntryAndFailsPastTheTotal()
    {
        using var stream = Zip(("a.txt", 100), ("b.txt", 40), ("c.txt", 40), ("d.txt", 40));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var budget = new ZipReadBudget(maxEntryBytes: 50, maxTotalBytes: 200);
        var entries = ZipReadBudget.Entries(archive).ToDictionary(entry => entry.FullName);

        Assert.Null(budget.Read(entries["a.txt"], TestContext.Current.CancellationToken));
        Assert.Equal(40, budget.Read(entries["b.txt"], TestContext.Current.CancellationToken)!.Length);
        Assert.Equal(40, budget.Read(entries["c.txt"], TestContext.Current.CancellationToken)!.Length);
        Assert.Throws<InvalidDataException>(() => budget.Read(entries["d.txt"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BudgetHonoursCancellation()
    {
        using var stream = Zip(("a.txt", 10));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ZipReadBudget(100, 100).Read(archive.Entries[0], source.Token));
    }

    [Fact]
    public void TooManyEntriesAreRejectedByBothReaders()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index <= ZipReadBudget.MaxEntries; index++)
            {
                archive.CreateEntry("e" + index, CompressionLevel.NoCompression);
            }
        }

        stream.Position = 0;
        var unpacker = PluginRegistry.Default.FindUnpacker("ZipEntries")!;
        var context = new PluginContext("many.zip", [], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<PluginException>(() => unpacker.Unpack(stream, context));

        stream.Position = 0;
        var error = Assert.Throws<InvalidDataException>(() => NuGetPackageReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Contains("entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedNuspecIsRejectedWithoutInflatingItAll()
    {
        // Spaces compress about a thousand to one, so this 2 MB nuspec is a few kilobytes in the archive.
        using var stream = Nupkg("<package><metadata><id>Example</id><version>1.0.0</version><description>"
            + new string(' ', 2 * (int)NuGetPackageReader.MaxNuspecBytes) + "</description></metadata></package>");

        var error = Assert.Throws<InvalidDataException>(() => NuGetPackageReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Contains(".nuspec is larger", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedNuspecIsRejectedWithoutOverflowing()
    {
        // XElement.Value recursed once per level and overflowed the stack on this.
        var depth = 100_000;
        using var stream = Nupkg("<package><metadata><id>"
            + string.Concat(Enumerable.Repeat("<a>", depth)) + "Example" + string.Concat(Enumerable.Repeat("</a>", depth))
            + "</id><version>1.0.0</version></metadata></package>");

        var error = Assert.Throws<InvalidDataException>(() => NuGetPackageReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Contains("levels deep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivesPastTheLengthCapAreNotOpened()
    {
        using var seekable = new LongStream();
        Assert.Throws<InvalidDataException>(() => NuGetPackageReader.Read(seekable, TestContext.Current.CancellationToken));
        var unpacker = PluginRegistry.Default.FindUnpacker("ZipEntries")!;
        var context = new PluginContext("long.zip", [], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<PluginException>(() => unpacker.Unpack(seekable, context));

        // A stream that cannot seek is copied only up to the cap, however long it runs.
        Assert.Throws<PluginException>(() => unpacker.Unpack(new EndlessStream(), context));
    }

    private static MemoryStream Nupkg(string nuspec)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Example.nuspec", CompressionLevel.SmallestSize).Open());
            writer.Write(nuspec);
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>An empty stream that reports a length past the archive cap.</summary>
    private sealed class LongStream : MemoryStream
    {
        public override long Length => ZipReadBudget.MaxArchiveBytes + 1;
    }

    /// <summary>A stream of zeros that never ends and cannot seek.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static MemoryStream Zip(params (string Name, int Length)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, length) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(new byte[length]);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
