using DiffRapid.Core.IO;

namespace DiffRapid.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "diffrapid-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void CreatesAndReplacesWithoutLeavingTemporaryFiles()
    {
        var path = Path.Combine(_folder, "sub", "out.txt");

        AtomicFile.WriteAllBytes(path, [1, 2, 3]);
        AtomicFile.WriteAllBytes(path, [4]);

        Assert.Equal([4], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void FailedWriteKeepsTheOldContentAndCleansUp()
    {
        var path = Path.Combine(_folder, "out.txt");
        File.WriteAllBytes(path, [9, 9]);

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, stream =>
        {
            stream.WriteByte(1);
            throw new InvalidOperationException("disk full");
        }));

        Assert.Equal([9, 9], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void ReadOnlyTargetIsReplacedOnlyWhenAsked()
    {
        var path = Path.Combine(_folder, "locked.txt");
        File.WriteAllBytes(path, [1]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<UnauthorizedAccessException>(() => AtomicFile.WriteAllBytes(path, [2]));
                Assert.Equal([1], File.ReadAllBytes(path));
            }

            AtomicFile.WriteAllBytes(path, [3], replaceReadOnly: true);
            Assert.Equal([3], File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
