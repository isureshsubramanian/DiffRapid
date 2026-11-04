using System.Text;
using DiffRapid.Cli;

namespace DiffRapid.Core.Tests;

public sealed class ConflictFileCliTests
{
    [Theory]
    [InlineData(false, false, "file.txt", 65001)]
    [InlineData(true, true, "file.part.txt", 65001)]
    [InlineData(true, true, "file", 1200)]
    [InlineData(false, true, "file.txt", 1201)]
    [InlineData(false, false, "file.txt", 28591)]
    public void WritesVersionsWithOriginalEncoding(bool outDirectory, bool includeBase, string name, int codePage)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var encoding = Encoding.GetEncoding(codePage);
            var input = Path.Combine(directory, name);
            var text = "head\r\n<<<<<<< ours\nété\r\n" + (includeBase ? "||||||| base\nold\n" : "") + "=======\nright\n>>>>>>> theirs\ntail";
            File.WriteAllText(input, text, encoding);
            var output = outDirectory ? Path.Combine(directory, "output") : directory;
            string[] args = outDirectory ? ["split-conflicts", input, "--out-dir", output] : ["split-conflicts", input];
            Assert.Equal(0, Program.Main(args));
            Check("left", "head\r\nété\r\ntail");
            Check("right", "head\r\nright\ntail");
            if (includeBase) Check("base", "head\r\nold\ntail");
            else Assert.False(File.Exists(OutputPath("base")));

            string OutputPath(string side) => Path.Combine(output, Path.GetFileNameWithoutExtension(name) + "." + side + Path.GetExtension(name));
            void Check(string side, string expected) => Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), File.ReadAllBytes(OutputPath(side)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ReturnsNoConflictsAndErrorsWithoutWritingVersions()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "file.txt");
            File.WriteAllText(input, "plain");
            Assert.Equal(1, Program.Main(["split-conflicts", input]));
            File.WriteAllText(input, "=======\n");
            Assert.Equal(2, Program.Main(["split-conflicts", input]));
            Assert.Equal(2, Program.Main(["split-conflicts"]));
            Assert.Equal(2, Program.Main(["split-conflicts", input, "--out-dir"]));
            Assert.Equal(2, Program.Main(["split-conflicts", Path.Combine(directory, "missing")]));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
