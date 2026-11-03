using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Git;
using DiffRapid.Core.VersionControl;

namespace DiffRapid.Core.Tests;

/// <summary>Verifies Git repository history, revision content, discovery, and CLI commands.</summary>
public sealed class GitRepositoryTests
{
    /// <summary>Verifies Git history, revision content, discovery, and CLI output.</summary>
    [Fact]
    public async Task HistoryContentDiscoveryAndCli()
    {
        using var repositoryFixture = new Repository();
        await repositoryFixture.Initialize();
        var original = "old \u00e9.txt";
        var renamed = "new \u00e9.txt";
        var bytes = Encoding.UTF8.GetBytes("h\u00e9llo\n");
        File.WriteAllBytes(Path.Combine(repositoryFixture.Root, original), bytes);
        await repositoryFixture.Git("add", "--", original);
        await repositoryFixture.Commit("first");
        var first = (await repositoryFixture.Git("rev-parse", "HEAD")).Text.Trim();
        await repositoryFixture.Git("mv", original, renamed);
        await repositoryFixture.Commit("rename");
        var file = Path.Combine(repositoryFixture.Root, renamed);
        var repository = GitRepository.Find(file)!;
        Assert.Equal(repositoryFixture.Root, repository.Root);
        Assert.Equal(renamed, repository.RelativePath(file));
        Assert.Throws<ArgumentException>(() => repository.RelativePath(Path.GetTempPath()));
        var history = await repository.GetHistoryAsync(file, 20, default);
        Assert.Equal(2, history.Count);
        Assert.Equal(renamed, history[0].PathAtCommit);
        Assert.Equal(original, history[1].PathAtCommit);
        Assert.Equal("Test Author", history[0].Author);
        Assert.Equal(2020, history[0].Date.Year);
        Assert.Equal(bytes, await repository.ReadFileAsync(first, original, default));
        Assert.Null(await repository.ReadFileAsync("HEAD", original, default));
        Assert.Equal(bytes, await repository.ReadFileAsync("HEAD~1", original, default));
        await repositoryFixture.Git("tag", "sample");
        Assert.Equal(bytes, await repository.ReadFileAsync("sample", renamed, default));
        Assert.Empty(await repository.GetHistoryAsync(file, 0, default));
        await Assert.ThrowsAsync<GitException>(() => repository.ReadFileAsync("unknown-revision", renamed, default));
        await VerifyRevisionCommands(repositoryFixture, repository, file, renamed);
        await VerifyDeletionBinaryContentAndWorktrees(repositoryFixture, repository, file, renamed);
    }

    private static async Task VerifyDeletionBinaryContentAndWorktrees(Repository repositoryFixture, GitRepository repository, string file, string renamed)
    {
        var nested = Path.Combine(repositoryFixture.Root, "nested", "folder");
        Directory.CreateDirectory(nested);
        Assert.Equal(repositoryFixture.Root, GitRepository.Find(nested)!.Root);
        await repositoryFixture.Git("rm", "-f", "--", renamed);
        await repositoryFixture.Commit("delete");
        Assert.Null(await repository.ReadFileAsync("HEAD", renamed, default));
        Assert.Equal(3, (await repository.GetHistoryAsync(file, 20, default)).Count);
        File.WriteAllBytes(Path.Combine(repositoryFixture.Root, "binary"), [0, 255, 1, 128]);
        await repositoryFixture.Git("add", "--", "binary");
        await repositoryFixture.Commit("binary");
        Assert.Equal(new byte[] { 0, 255, 1, 128 }, await repository.ReadFileAsync("HEAD", "binary", default));
        Assert.Equal(2, Program.Main(["diff", Path.Combine(repositoryFixture.Root, "binary"), "--rev", "HEAD", "--quiet"]));
        var worktree = Path.Combine(repositoryFixture.Root, "worktree");
        await repositoryFixture.Git("worktree", "add", "--detach", worktree);
        Assert.Equal(worktree, GitRepository.Find(Path.Combine(worktree, "binary"))!.Root);
        await repositoryFixture.Git("worktree", "remove", "--force", worktree);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadFileAsync("HEAD", "binary", cancelled.Token));
    }

    private static async Task VerifyRevisionCommands(Repository repositoryFixture, GitRepository repository, string file, string renamed)
    {
        Assert.Equal(0, Program.Main(["diff", file, "--rev", "HEAD", "--quiet"]));
        File.WriteAllText(file, "staged\n");
        await repositoryFixture.Git("add", "--", renamed);
        Assert.Equal(Encoding.UTF8.GetBytes("staged\n"), await repository.ReadFileAsync(":0", renamed, default));
        Assert.Equal(0, Program.Main(["diff", file, "--rev", ":0", "--quiet"]));
        Assert.Equal(1, Program.Main(["diff", file, "--rev", "HEAD", "--quiet"]));
        Assert.Equal(2, Program.Main(["diff", file, "--rev", "unknown-revision", "--quiet"]));
        Assert.Equal(0, Program.Main(["log", file, "-n", "1"]));
        var cli = typeof(Program).Assembly.Location;
        var diff = await new ProcessRunner().RunAsync("dotnet", repositoryFixture.Root, [cli, "diff", file, "--rev", "HEAD"], default);
        Assert.Equal(1, diff.ExitCode);
        Assert.Contains(file + "@HEAD", diff.Text);
        Assert.Contains("staged", diff.Text);
        var log = await new ProcessRunner().RunAsync("dotnet", repositoryFixture.Root, [cli, "log", file, "-n", "1"], default);
        Assert.Equal(0, log.ExitCode);
        Assert.Contains("2020-01-02 Test Author rename", log.Text);
        var help = await new ProcessRunner().RunAsync("dotnet", repositoryFixture.Root, [cli, "--help"], default);
        Assert.Contains("--rev", help.Text);
        Assert.Contains("diffrapid log", help.Text);
        var argument = "space and \"quotes\" and trailing\\";
        var quoted = await repositoryFixture.Git("-c", "test.quoted=" + argument, "config", "--get", "test.quoted");
        Assert.Equal(argument, quoted.Text.TrimEnd('\r', '\n'));
        Assert.Equal(2, Program.Main(["log"]));
    }

    /// <summary>Verifies paths outside a Git repository are rejected.</summary>
    [Fact]
    public void OutsideRepository()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(GitRepository.Find(folder));
            Assert.Equal(2, Program.Main(["diff", Path.Combine(folder, "file"), "--rev", "HEAD", "--quiet"]));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        internal async Task Initialize()
        {
            Directory.CreateDirectory(Root);
            try
            {
                await Git("--version");
            }
            catch (IOException)
            {
                Assert.Skip("git is not on PATH.");
            }

            await Git("init");
            await Git("config", "user.name", "Test Author");
            await Git("config", "user.email", "test@example.invalid");
            await Git("config", "commit.gpgsign", "false");
        }

        internal Task<ProcessResult> Commit(string subject) => Git("-c", "user.name=Test Author", "commit", "-m", subject, "--date=2020-01-02T03:04:05Z");
        internal async Task<ProcessResult> Git(params string[] args)
        {
            var result = await new ProcessRunner().RunAsync("git", Root, args, default);
            Assert.True(result.ExitCode == 0, result.Error);
            return result;
        }

        /// <summary>Deletes the temporary repository and its files.</summary>
        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, true);
        }
    }
}
