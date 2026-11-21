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
