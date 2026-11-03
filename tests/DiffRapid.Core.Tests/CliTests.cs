using DiffRapid.Cli;

namespace DiffRapid.Core.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("-o")]
    [InlineData("/o")]
    public void ParsesGitMergeArguments(string outputOption)
    {
        var args = Program.ParsedArgs.Parse(
            ["merge", "base file", "local file", "remote file", outputOption, "merged file",
                "/dl", "Local label", "/dm", "Base label", "/dr", "Remote label"], 1);

        Assert.Equal(["base file", "local file", "remote file"], args.Positionals);
        Assert.Equal("merged file", args.Value("output"));
        Assert.Equal("Local label", args.Value("left-label"));
        Assert.Equal("Base label", args.Value("base-label"));
        Assert.Equal("Remote label", args.Value("right-label"));
    }

    [Fact]
    public void ParsesGitDiffArguments()
    {
        var args = Program.ParsedArgs.Parse(["diff", "local file", "remote file"], 1);
        Assert.Equal(["local file", "remote file"], args.Positionals);
    }

    [Theory]
    [InlineData(false, "-o", false)]
    [InlineData(true, "/o", false)]
    [InlineData(true, "-o", true)]
    public void MergeWritesOutputAndReturnsConflictStatus(bool conflict, string outputOption, bool customLabels)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var basePath = Path.Combine(directory, "base file.txt");
            var localPath = Path.Combine(directory, "local file.txt");
            var remotePath = Path.Combine(directory, "remote file.txt");
            var mergedPath = Path.Combine(directory, "merged file.txt");
            File.WriteAllText(basePath, "original\n");
            File.WriteAllText(localPath, "local\n");
            File.WriteAllText(remotePath, conflict ? "remote\n" : "original\n");
            string[] args = ["merge", basePath, localPath, remotePath, outputOption, mergedPath, "--diff3"];
            if (customLabels)
            {
                args = [.. args, "/dl", "LOCAL", "/dm", "BASE", "/dr", "REMOTE"];
            }

            Assert.Equal(conflict ? 1 : 0, Program.Main(args));
            var text = File.ReadAllText(mergedPath);
            if (conflict)
            {
                Assert.Contains("<<<<<<< " + (customLabels ? "LOCAL" : "local file.txt"), text);
                Assert.Contains("||||||| " + (customLabels ? "BASE" : "base file.txt"), text);
                Assert.Contains("=======", text);
                Assert.Contains(">>>>>>> " + (customLabels ? "REMOTE" : "remote file.txt"), text);
            }
            else
            {
                Assert.Equal("local\n", text);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitConfigurationQuotesWindowsPathsAndPreservesGitVariables(bool global)
    {
        var scope = global ? " --global" : string.Empty;
        var commands = GitConfiguration.Format(@"C:\Program Files\DiffRapid\diffrapid.exe", global);
        Assert.Equal(string.Join(Environment.NewLine,
            $"git config{scope} difftool.diffrapid.cmd ''\"'\"'C:/Program Files/DiffRapid/diffrapid.exe'\"'\"' diff \"$LOCAL\" \"$REMOTE\"'",
            $"git config{scope} mergetool.diffrapid.cmd ''\"'\"'C:/Program Files/DiffRapid/diffrapid.exe'\"'\"' merge \"$BASE\" \"$LOCAL\" \"$REMOTE\" -o \"$MERGED\"'",
            $"git config{scope} mergetool.diffrapid.trustExitCode true"), commands);
    }

    [Fact]
    public void GitConfigurationEscapesApostrophes()
    {
        var commands = GitConfiguration.Format("C:/User's Tools/diffrapid.exe", false);
        Assert.DoesNotContain("C:/User's Tools", commands);
        Assert.Contains("$MERGED", commands);
    }

    [Fact]
    public void GitConfigurationRejectsUnexpectedArguments()
    {
        Assert.Equal(2, Program.Main(["git-config", "--local"]));
        Assert.Equal(2, Program.Main(["git-config", "--global", "extra"]));
    }
}
