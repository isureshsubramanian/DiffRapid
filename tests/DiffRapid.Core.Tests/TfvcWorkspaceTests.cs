using System.Text;
using DiffRapid.Core.Git;
using DiffRapid.Core.VersionControl;

namespace DiffRapid.Core.Tests;

/// <summary>Verifies TFVC workspace discovery, parsing, and shared process execution.</summary>
public sealed class TfvcWorkspaceTests
{
    /// <summary>Verifies executable overrides, installer results, and PATH fallback.</summary>
    [Fact]
    public void DiscoversExecutableFromOverrideInstallerOrPath()
    {
        var installer = Path.GetTempFileName();
        var runner = new FakeRunner
        {
            Handler = arguments =>
            {
                Assert.Equal(new[] { "-latest", "-requires", "Microsoft.VisualStudio.TeamExplorer", "-find", "**\\tf.exe" }, arguments);
                return new ProcessResult(0, Encoding.UTF8.GetBytes("first tf.exe\r\nsecond tf.exe\r\n"), "");
            },
        };

        try
        {
            Assert.Equal("override tf", TfvcWorkspace.Locate(runner, installer, "override tf"));
            Assert.Null(runner.Arguments);
            Assert.Equal("first tf.exe", TfvcWorkspace.Locate(runner, installer, null));
            runner.Handler = _ => new ProcessResult(0, [], "");
            Assert.Equal("tf", TfvcWorkspace.Locate(runner, installer, null));
            runner.Handler = _ => new ProcessResult(1, Encoding.UTF8.GetBytes("ignored tf.exe"), "failed");
            Assert.Equal("tf", TfvcWorkspace.Locate(runner, installer, null));
        }
        finally
        {
            File.Delete(installer);
        }

        Assert.Equal("tf", TfvcWorkspace.Locate(runner, installer, null));
    }

    /// <summary>Verifies multiline comments and unsupported changeset diagnostics.</summary>
    [Fact]
    public void ParsesChangesetCommentsAndRejectsUnsupportedHeaders()
    {
        var history = TfvcWorkspace.ParseHistory("Changeset: 42\nUser: Alice\nDate: 2020-01-02T03:04:05Z\nComment:\n  Description\nCheck-in Notes:\n  Excluded", "file");
        Assert.Equal("Description", Assert.Single(history).Subject);
        Assert.Throws<IOException>(() => TfvcWorkspace.ParseHistory("Changeset: invalid\nDate: 2020-01-02T03:04:05Z", "file"));
        Assert.Throws<IOException>(() => TfvcWorkspace.ParseHistory("Changeset unsupported", "file"));
    }

    /// <summary>Verifies process output, diagnostics, exit codes, and startup errors.</summary>
    [Fact]
    public async Task ProcessRunnerPreservesStreamsAndWrapsStartFailures()
    {
        var runner = new ProcessRunner();
        var missingExecutable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(missingExecutable, Path.GetTempPath(), [], default));
        Assert.Contains(missingExecutable, error.Message);
        Assert.NotNull(error.InnerException);
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('output'); [Console]::Error.Write('diagnostic'); exit 7"]
            : ["-c", "printf output; printf diagnostic >&2; exit 7"];
        var result = await runner.RunAsync(executable, Path.GetTempPath(), arguments, default);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("output", result.Text);
        Assert.Equal("diagnostic", result.Error);
    }

    /// <summary>Verifies Git status parsing and rename tracking.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHistoryParsesStatusFieldsAndTracksEarlierNames(bool renamed)
    {
        var newest = new string('a', 40);
        var oldest = new string('b', 40);
        var status = renamed ? "\nR100\0old.txt\0new.txt" : "\nM\0new.txt";
        var output = $"\0{newest}\0aaaaaaa\0Alice\02020-01-02T03:04:05Z\0Newest\0{status}\0" +
            $"\0{oldest}\0bbbbbbb\0Bob\02020-01-01T03:04:05Z\0Oldest\0\nA\0old.txt\0";
        var runner = new FakeRunner
        {
            Handler = _ => new ProcessResult(0, Encoding.UTF8.GetBytes(output), "")
        };

        IVersionControl repository = new GitRepository(Path.GetTempPath(), "git", runner);
        var history = await repository.GetHistoryAsync(Path.Combine(Path.GetTempPath(), "new.txt"), 2, default);
        Assert.Equal(2, history.Count);
        Assert.Equal(newest, history[0].Id);
        Assert.Equal("new.txt", history[0].PathAtVersion);
        Assert.Equal(renamed ? "old.txt" : "new.txt", history[1].PathAtVersion);
        Assert.Equal("Bob", history[1].Author);
    }

    /// <summary>Verifies detailed history and the most specific workspace mapping.</summary>
    [Fact]
    public void ParsesDetailedHistoryAndWorkspaceMappings()
    {
        const string output = """
            Changeset: 123
            User: DOMAIN\alice
            Date: 2020-01-02T03:04:05Z

            Comment:
              Fix comparison
              Preserve encoding
            Items:
              edit $/Project/file.txt
            ------------------------------------------------------------------------
            Changeset: 122
            User: Bob
            Date: Thursday, January 2, 2020 3:00:00 AM

            Comment:
              Initial file
            Items:
              add $/Project/file.txt
            """;
        var history = TfvcWorkspace.ParseHistory(output, "file.txt");
        Assert.Equal(2, history.Count);
        Assert.Equal("C123", history[0].Id);
        Assert.Equal("DOMAIN\\alice", history[0].Author);
        Assert.Contains("Preserve encoding", history[0].Subject);
        Assert.Equal(TimeSpan.Zero, history[0].Date.Offset);
        Assert.Equal("file.txt", history[0].PathAtVersion);
        var root = Path.Combine(Path.GetTempPath(), "workspace");
        var nested = Path.Combine(root, "nested");
        var mapping = $"Workspace: example\n$/Project: {root}\n$/Other: {nested}";
        Assert.Equal(nested, TfvcWorkspace.ParseWorkfold(mapping, Path.Combine(nested, "file")));
        Assert.Null(TfvcWorkspace.ParseWorkfold(mapping, Path.GetTempPath()));
        Assert.Empty(TfvcWorkspace.ParseHistory("No history entries were found.", "file"));
        Assert.Throws<IOException>(() => TfvcWorkspace.ParseHistory("Changeset: 1\nDate: invalid", "file"));
    }

    /// <summary>Verifies TFVC arguments, revision bytes, failures, and temporary file cleanup.</summary>
    [Fact]
    public async Task BuildsArgumentsReadsBytesAndDeletesTemporaryOutput()
    {
        var runner = new FakeRunner();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var workspace = new TfvcWorkspace(root, "custom tf", runner);
        runner.Handler = args =>
        {
            Assert.Equal(["vc", "history", Path.Combine(root, "file \u00e9.txt"), "/noprompt", "/format:detailed", "/stopafter:5"], args);
            return new ProcessResult(0, Encoding.UTF8.GetBytes("Changeset: 123\nUser: Alice\nDate: 2020-01-02T03:04:05Z\nComment:\n  Fix\nItems:\n"), "");
        };

        var history = await workspace.GetHistoryAsync(Path.Combine(root, "file \u00e9.txt"), 5, default);
        Assert.Single(history);
        string? output = null;
        runner.Handler = args =>
        {
            Assert.Equal("view", args[1]);
            Assert.Equal("/version:C123", args[3]);
            Assert.Equal("/noprompt", args[4]);
            output = args[5].Substring("/output:".Length);
            File.WriteAllBytes(output, [0, 255, 1]);
            return new ProcessResult(0, [], "");
        };

        Assert.Equal(new byte[] { 0, 255, 1 }, await workspace.ReadFileAsync("C123", "file \u00e9.txt", default));
        Assert.False(File.Exists(output));
        foreach (var version in new[] { "T", "W" })
        {
            runner.Handler = args =>
            {
                Assert.Equal("/version:" + version, args[3]);
                return new ProcessResult(1, [], "TF14098: The item does not exist.");
            };

            Assert.Null(await workspace.ReadFileAsync(version, "file", default));
        }

        runner.Handler = _ => new ProcessResult(1, [], "Access denied");
        var error = await Assert.ThrowsAsync<IOException>(() => workspace.ReadFileAsync("C123", "file", default));
        Assert.Contains("tf:", error.Message);
        await Assert.ThrowsAsync<IOException>(() => workspace.GetHistoryAsync("file", 5, default));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.ReadFileAsync("-bad", "file", default));
        Assert.Empty(await workspace.GetHistoryAsync("file", 0, default));
    }

    /// <summary>Verifies shared Git process arguments and failure diagnostics.</summary>
    [Fact]
    public async Task GitUsesSharedRunnerAndPreservesFailureDiagnostics()
    {
        var runner = new FakeRunner
        {
            Handler = _ => new ProcessResult(1, [], "fatal: test failure")
        };

        var repository = new GitRepository(Path.GetTempPath(), "custom git", runner);
        var error = await Assert.ThrowsAsync<GitException>(() => repository.GetHistoryAsync(Path.Combine(Path.GetTempPath(), "file"), 1, default));
        Assert.Contains("fatal: test failure", error.Message);
        Assert.Contains("--follow", runner.Arguments!);
        Assert.Contains("core.quotepath=false", runner.Arguments!);
        Assert.Contains("log.showSignature=false", runner.Arguments!);
    }

    /// <summary>Verifies cancellation terminates a running process.</summary>
    [Fact]
    public async Task RunningProcessIsCancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sleep";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]
            : ["30"];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(executable, Path.GetTempPath(), arguments, cancellation.Token));
    }

    /// <summary>Verifies discovery through workspace mappings and metadata.</summary>
    [Fact]
    public void FindsWorkspaceByMappingOrMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file");
            var runner = new FakeRunner
            {
                Handler = args =>
                {
                    Assert.Equal(["vc", "workfold", file, "/noprompt"], args);
                    return new ProcessResult(0, Encoding.UTF8.GetBytes("$/Project: " + root), "");
                },
            };

            Assert.Equal(root, TfvcWorkspace.Find(file, "fake tf", runner)!.Root);
            runner.Handler = _ => new ProcessResult(1, [], "Not mapped");
            Assert.Null(TfvcWorkspace.Find(file, "fake tf", runner));
            foreach (var marker in new[] { "$tf", ".tf" })
            {
                Directory.CreateDirectory(Path.Combine(root, marker));
                Assert.Equal(root, TfvcWorkspace.Find(file, "fake tf", runner)!.Root);
                Directory.Delete(Path.Combine(root, marker));
            }

            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "$tf"));
            Assert.Equal("git", DiffRapid.Core.VersionControl.VersionControl.Find(file)!.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        internal Func<IReadOnlyList<string>, ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, [], "");

        internal IReadOnlyList<string>? Arguments { get; private set; }

        /// <summary>Returns the configured response and records the command arguments.</summary>
        public Task<ProcessResult> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Arguments = arguments;
            return Task.FromResult(Handler(arguments));
        }
    }
}
