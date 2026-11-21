using DiffRapid.Core.IO;

namespace DiffRapid.Core.Tests;

public sealed class ExecutablePathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "diffrapid-exe-" + Guid.NewGuid().ToString("N"));

    public ExecutablePathTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string FileName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static void WriteProgram(string path, bool executable = true)
    {
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void RelativePathEntriesAndTheCurrentFolderAreSkipped()
    {
        // The planted folder sits below the current folder, so its PATH entry is truly relative: from a temp folder on
        // another drive (as on CI, C: temp and D: checkout) GetRelativePath gives back an absolute path.
        var trusted = Path.Combine(_folder, "trusted");
        var relative = "diffrapid-planted-" + Guid.NewGuid().ToString("N");
        var planted = Path.Combine(Environment.CurrentDirectory, relative);
        Directory.CreateDirectory(trusted);
        Directory.CreateDirectory(planted);
        try
        {
            WriteProgram(Path.Combine(planted, FileName("tool")));
            WriteProgram(Path.Combine(trusted, FileName("tool")));
            Assert.False(Path.IsPathRooted(relative));
            var path = string.Join(Path.PathSeparator, "", ".", relative, trusted);

            Assert.Equal(Path.Combine(trusted, FileName("tool")), ExecutablePath.Search("tool", path));
            Assert.Null(ExecutablePath.Search("tool", string.Join(Path.PathSeparator, ".", relative)));
        }
        finally
        {
            Directory.Delete(planted, recursive: true);
        }
    }

    [Fact]
    public void ProgramsWithoutAnExecuteBitAreSkippedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows files have no execute bit.");
        }

        var first = Path.Combine(_folder, "first");
        var second = Path.Combine(_folder, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteProgram(Path.Combine(first, "git"), executable: false);
        WriteProgram(Path.Combine(second, "git"));

        Assert.Equal(Path.Combine(second, "git"), ExecutablePath.Search("git", string.Join(Path.PathSeparator, first, second)));
        Assert.Null(ExecutablePath.Search("git", first));
    }

    [Fact]
    public void PathsAreKeptAndMissingProgramsFail()
    {
        Assert.Equal("./tool", ExecutablePath.Resolve("./tool"));
        Assert.Equal(Path.Combine(_folder, "tool"), ExecutablePath.Resolve(Path.Combine(_folder, "tool")));
        Assert.Throws<FileNotFoundException>(() => ExecutablePath.Resolve("diffrapid-no-such-program-" + Guid.NewGuid().ToString("N")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.StartsWith("/", ExecutablePath.Resolve("sh"));
        }
    }
}
