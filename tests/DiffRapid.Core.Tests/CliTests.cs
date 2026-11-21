using DiffRapid.Cli;

namespace DiffRapid.Core.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task DiffAppliesFiltersAndReportsInvalidArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.txt");
            var right = Path.Combine(directory, "right.txt");
            File.WriteAllText(left, "// old\nversion 1\n");
            File.WriteAllText(right, "// new\nversion 2\n");
            Assert.Equal(1, await Program.Main(["diff", left, right, "--quiet"]));
            Assert.Equal(0, await Program.Main(["diff", left, right, "--quiet",
                "--line-filter", "^//", "--substitute", @"version \d+=version"]));
            Assert.Equal(2, await Program.Main(["diff", left, right, "--quiet", "--line-filter", "["]));
            Assert.Equal(2, await Program.Main(["diff", left, right, "--substitute", "missing-separator"]));
            Assert.Equal(2, await Program.Main(["diff", left, right, "--line-filter"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnexpectedErrorsAndCancellationExitWithMessageOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var error = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var left = Path.Combine(directory, "left.txt");
            File.WriteAllText(left, "a\n");

            // An unknown code page throws NotSupportedException, which no command handles itself.
            Assert.Equal(2, await Program.Main(["diff", left, left, "--cp", "12345"]));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Equal(2, await Program.RunAsync(["diff", left, left], cancellation.Token));
            Assert.Contains("diffrapid: Cancelled.", captured.ToString());
            Assert.DoesNotContain("   at ", captured.ToString());
        }
        finally
        {
            Console.SetError(error);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParsesRepeatedFiltersAndPreservesReplacementEquals()
    {
        var args = Program.ParsedArgs.Parse(["diff", "left", "right",
            "--line-filter", "^//", "--line-filter=^#",
            "--substitute", "v[0-9]+=version=stable", "--substitute", "build=release"], 1);
        var options = args.ToCompareOptions();

        Assert.Equal(["^//", "^#"], options.LineFilters.Select(f => f.Pattern));
        Assert.Equal(["v[0-9]+", "build"], options.Substitutions.Select(f => f.Find));
        Assert.Equal(["version=stable", "release"], options.Substitutions.Select(f => f.Replace));
        Assert.All(options.Substitutions, f => Assert.True(f.IsRegex && f.CaseSensitive && !f.WholeWord));
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("/o")]
    public void ParsesGitMergeArguments(string outputOption)
    {
        var args = Program.ParsedArgs.Parse(
            ["merge", "base file", "local file", "remote file", Native(outputOption), "merged file",
                Native("/dl"), "Local label", Native("/dm"), "Base label", Native("/dr"), "Remote label"], 1);

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

    /// <summary>WinMerge-style "/o" options are Windows only; elsewhere the same token is the dash form's path.</summary>
    internal static string Native(string option) => OperatingSystem.IsWindows() || !option.StartsWith('/') ? option : "-" + option[1..];

    [Fact]
    public void SlashArgumentsArePathsOutsideWindows()
    {
        var args = Program.ParsedArgs.Parse(["diff", "/tmp/left", "/o"], 1);
        Assert.Equal(OperatingSystem.IsWindows() ? ["/tmp/left"] : ["/tmp/left", "/o"], args.Positionals);
    }

    [Theory]
    [InlineData("'--dryrun'", "sync", "a", "b", "--profile", "mirror-right", "--yes", "--dryrun")]
    [InlineData("'--report-format'", "diff", "a", "b", "--report-format", "html")]
    [InlineData("'-x'", "table", "a", "b", "-x")]
    [InlineData("'--temperature=1'", "explain", "a", "b", "--temperature=1")]
    [InlineData("'--lable'", "history", "list", "a", "--lable", "x")]
    [InlineData("'--fuzz'", "image", "a", "b", "--fuzz", "1")]
    [InlineData("does not take a value", "diff", "a", "b", "--quiet=yes")]
    [InlineData("'--left-label' needs a value", "diff", "a", "b", "--left-label", "--quiet")]
    [InlineData("--api-key-env", "explain", "a", "b", "--api-key=secret")]
    public void UnknownOptionsSwitchValuesAndMissingValuesAreUsageErrors(string expected, params string[] arguments)
    {
        var error = Assert.Throws<Program.UsageException>(() => Program.ParsedArgs.Parse(arguments, 1));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task UnknownSyncOptionFailsBeforeAnyChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(directory, "left"));
        Directory.CreateDirectory(Path.Combine(directory, "right"));
        File.WriteAllText(Path.Combine(directory, "right", "keep.txt"), "keep");
        try
        {
            Assert.Equal(2, await Program.Main(["sync", Path.Combine(directory, "left"), Path.Combine(directory, "right"),
                "--profile", "mirror-right", "--yes", "--dryrun"]));
            Assert.True(File.Exists(Path.Combine(directory, "right", "keep.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutputFilesAreReplacedOnlyWhenComplete()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "out.txt");
            File.WriteAllText(output, "previous");
            Assert.Throws<IOException>(() => Program.WriteText(output, writer =>
            {
                writer.Write(new string('x', 100_000));
                throw new IOException("disk full");
            }));
            Assert.Equal("previous", File.ReadAllText(output));
            Assert.Equal([output], Directory.GetFiles(directory));

            // A patch and a merge result written with -o replace the file whole.
            var left = Path.Combine(directory, "left.txt");
            var right = Path.Combine(directory, "right.txt");
            File.WriteAllText(left, "a\n");
            File.WriteAllText(right, "b\n");
            Assert.Equal(1, await Program.Main(["patch", left, right, "-o", output]));
            Assert.Equal("--- a/left.txt\n+++ b/left.txt\n@@ -1 +1 @@\n-a\n+b\n", File.ReadAllText(output));
            Assert.Equal(0, await Program.Main(["merge", left, left, right, "-o", output]));
            Assert.Equal("b\n", File.ReadAllText(output));
            Assert.Equal(3, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutputToDevicesAndPipesIsWrittenInPlace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.txt");
            var right = Path.Combine(directory, "right.txt");
            File.WriteAllText(left, "a\n");
            File.WriteAllText(right, "b\n");

            // No temporary file can be created in /dev, so -o /dev/stdout must write to the device itself.
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { typeof(Program).Assembly.Location, "patch", left, right, "-o", "/dev/stdout" })
            {
                start.ArgumentList.Add(argument);
            }

            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal("", await error);
                Assert.Equal(1, process.ExitCode);
                Assert.Equal("--- a/left.txt\n+++ b/left.txt\n@@ -1 +1 @@\n-a\n+b\n", await output);
            }

            // A named pipe, as the shell's >(command) gives, is written in place too.
            var pipe = Path.Combine(directory, "pipe");
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [pipe]))
            {
                await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, mkfifo.ExitCode);
            }

            // Opening a pipe blocks until the other end opens, so the reader opens it on its own thread.
            var reader = Task.Run(() => File.ReadAllText(pipe), TestContext.Current.CancellationToken);
            var writer = Task.Run(() => Program.WriteText(pipe, text => text.Write("piped")), TestContext.Current.CancellationToken);
            await writer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("piped", await reader.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            // An empty regular file is still replaced atomically.
            var empty = Path.Combine(directory, "empty.txt");
            File.WriteAllText(empty, "");
            Assert.Throws<IOException>(() => Program.WriteText(empty, text =>
            {
                text.Write("partial");
                throw new IOException("disk full");
            }));
            Assert.Equal("", File.ReadAllText(empty));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Skip = "Needs the AtomicFile change that keeps the Unix file mode; remove this Skip once it is merged.")]
    public async Task MergeOutputKeepsTheFileMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var basePath = Path.Combine(directory, "base.sh");
            var remote = Path.Combine(directory, "remote.sh");
            var script = Path.Combine(directory, "script.sh");
            File.WriteAllText(basePath, "echo a\n");
            File.WriteAllText(remote, "echo b\n");
            File.WriteAllText(script, "echo a\n");
            var executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(script, executable);
            Assert.Equal(0, await Program.Main(["merge", basePath, script, remote, "-o", script]));
            Assert.Equal("echo b\n", File.ReadAllText(script));
            Assert.Equal(executable, File.GetUnixFileMode(script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("plain\ttext", false, "plain\ttext")]
    [InlineData("\u001b]0;title\u0007\u001b[31mred", false, "\\x1B]0;title\\x07\\x1B[31mred")]
    [InlineData("a\r\nb\u009bc\u007f", false, "a\\x0D\\x0Ab\\x9Bc\\x7F")]
    [InlineData("a\nb\rc", true, "a\nb\\x0Dc")]
    [InlineData("café 日本", false, "café 日本")]
    public void TerminalTextEscapesControlCharacters(string text, bool keepLineFeeds, string expected)
    {
        Assert.Equal(expected, TerminalText.Escape(text, keepLineFeeds));
    }

    [Fact]
    public void ConsoleOutputEscapesFileContentAndNames()
    {
        var differences = DiffRapid.Core.Structured.StructuredComparer.Compare("{\"a\": \"x\"}", "{\"a\": \"\\u001b[2Jy\", \"k\\u0007\": 1}",
            new DiffRapid.Core.Structured.StructuredCompareOptions { Format = DiffRapid.Core.Structured.StructuredFormat.Json }).Differences;
        var lines = string.Join("\n", differences.Select(Program.FormatDifference));
        Assert.DoesNotContain('\u001b', lines);
        Assert.DoesNotContain('\u0007', lines);
        Assert.Contains("\\x1B[2Jy", lines);
        Assert.Contains("\\x07", lines);
    }

    [Fact]
    public async Task UsageListsEachCommandOnce()
    {
        var output = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(["--help"]));
        }
        finally
        {
            Console.SetOut(output);
        }

        Assert.Single(captured.ToString().Split('\n'), line => line.TrimStart().StartsWith("diffrapid git-config", StringComparison.Ordinal));
    }

    [Fact]
    public void DoubleDashEndsOptions()
    {
        var args = Program.ParsedArgs.Parse(["diff", "--quiet", "--", "--left", "-o"], 1);
        Assert.True(args.Has("quiet"));
        Assert.Equal(["--left", "-o"], args.Positionals);
        var table = Program.ParsedArgs.Parse(["table", "--key=id", "--key", "name", "--", "-a.csv", "b.csv"], 1);
        Assert.Equal(["id", "name"], table.Values("key"));
        Assert.Equal(["-a.csv", "b.csv"], table.Positionals);
    }

    [Theory]
    [InlineData(false, "-o", false)]
    [InlineData(true, "/o", false)]
    [InlineData(true, "-o", true)]
    public async Task MergeWritesOutputAndReturnsConflictStatus(bool conflict, string outputOption, bool customLabels)
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
            string[] args = ["merge", basePath, localPath, remotePath, Native(outputOption), mergedPath, "--diff3"];
            if (customLabels)
            {
                args = [.. args, Native("/dl"), "LOCAL", Native("/dm"), "BASE", Native("/dr"), "REMOTE"];
            }

            Assert.Equal(conflict ? 1 : 0, await Program.Main(args));
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
    public async Task GitConfigurationRejectsUnexpectedArguments()
    {
        Assert.Equal(2, await Program.Main(["git-config", "--local"]));
        Assert.Equal(2, await Program.Main(["git-config", "--global", "extra"]));
    }
}
