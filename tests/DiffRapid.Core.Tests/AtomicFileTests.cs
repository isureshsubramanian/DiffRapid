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
    public void FailedCheckBeforeReplaceKeepsTheOldContentAndCleansUp()
    {
        var path = Path.Combine(_folder, "out.txt");
        File.WriteAllBytes(path, [9, 9]);
        string? checkedPath = null;

        Assert.Throws<IOException>(() => AtomicFile.Write(path, stream => stream.WriteByte(1), beforeReplace: temp =>
        {
            checkedPath = temp;
            Assert.Equal([1], File.ReadAllBytes(temp));
            throw new IOException("mismatch");
        }));

        Assert.NotEqual(path, checkedPath);
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
            Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.WriteAllBytes(path, [2]));
            Assert.Equal([1], File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(_folder));

            AtomicFile.WriteAllBytes(path, [3], replaceReadOnly: true);
            Assert.Equal([3], File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ReplacedFileKeepsItsUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows files have no Unix mode.");
            return; // Tells the platform analyzer that the rest runs on Unix only.
        }

        var path = Path.Combine(_folder, "run.sh");
        File.WriteAllBytes(path, [1]);
        const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, Executable);

        AtomicFile.WriteAllBytes(path, [2]);

        Assert.Equal([2], File.ReadAllBytes(path));
        Assert.Equal(Executable, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LinkedTargetStaysALinkAndItsFileIsReplaced()
    {
        var real = Path.Combine(_folder, "real.txt");
        var link = Path.Combine(_folder, "link.txt");
        File.WriteAllBytes(real, [1]);
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links cannot be created here.");
        }

        AtomicFile.WriteAllBytes(link, [2]);

        Assert.Equal(real, new FileInfo(link).LinkTarget);
        Assert.Equal([2], File.ReadAllBytes(real));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void TemporaryNameFitsBesideALongTargetName()
    {
        var path = Path.Combine(_folder, new string('n', 246) + ".txt");
        try
        {
            File.WriteAllBytes(path, [1]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This file system does not allow a 250-character name.");
        }

        File.Delete(path);
        AtomicFile.WriteAllBytes(path, [2]);
        AtomicFile.WriteAllBytes(path, [3]);

        Assert.Equal([3], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }
}
