using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Core.VersionControl;

namespace DiffRapid.Core.Git;

/// <summary>A failure reported by the Git executable.</summary>
public sealed class GitException : IOException
{
    /// <summary>Creates an exception with the Git executable's diagnostic message.</summary>
    public GitException(string message) : base("git: " + message.Trim())
    {
    }
}

/// <summary>A commit that changed a file, including its name at that commit.</summary>
/// <param name="Sha">The full commit identifier.</param>
/// <param name="ShortSha">The abbreviated commit identifier.</param>
/// <param name="Author">The author's display name.</param>
/// <param name="Date">The author date.</param>
/// <param name="Subject">The commit subject.</param>
/// <param name="PathAtCommit">The repository-relative path at this commit.</param>
public sealed record GitCommit(string Sha, string ShortSha, string Author, DateTimeOffset Date, string Subject, string PathAtCommit);

/// <summary>Reads file revisions using the Git command line.</summary>
public sealed class GitRepository : IVersionControl
{
    private readonly IProcessRunner _runner;
    private readonly string _executable;

    /// <summary>Executable used by subsequently discovered repositories; defaults to Git on PATH.</summary>
    public static string GitExecutablePath { get; set; } = "git";

    /// <summary>The absolute working tree root.</summary>
    public string Root { get; }

    /// <summary>The version control provider identifier.</summary>
    public string Kind => "git";

    internal GitRepository(string root, string executable, IProcessRunner runner)
    {
        Root = Path.GetFullPath(root);
        _executable = executable;
        _runner = runner;
    }

    /// <summary>Finds the enclosing working tree, including linked worktrees.</summary>
    public static GitRepository? Find(string path)
    {
        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        while (directory != null)
        {
            var marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return new GitRepository(directory, GitExecutablePath, new ProcessRunner());
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Returns a repository-relative path using forward slashes.</summary>
    public string RelativePath(string fullPath)
    {
        var root = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The file is outside the repository.", nameof(fullPath));
        }

        return full.Substring(root.Length).Replace('\\', '/');
    }

    private Task<ProcessResult> Run(CancellationToken cancellationToken, params string[] arguments)
    {
        var allArguments = new List<string>
        {
            "-c",
            "core.quotepath=false",
            "--no-pager",
            "-c",
            "log.showSignature=false",
            "-c",
            "i18n.logOutputEncoding=UTF-8"
        };

        allArguments.AddRange(arguments);
        return _runner.RunAsync(_executable, Root, allArguments, cancellationToken);
    }

    /// <summary>Lists commits newest first, following renames to their earlier paths.</summary>
    public async Task<IReadOnlyList<GitCommit>> GetHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        if (maxCount == 0)
        {
            return Array.Empty<GitCommit>();
        }

        var path = RelativePath(filePath);
        var result = await Run(cancellationToken,
            "log", "--follow", "-z", "--name-status", "--format=%x00%H%x00%h%x00%an%x00%aI%x00%s",
            "-n", maxCount.ToString(CultureInfo.InvariantCulture), "--", path).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new GitException(result.Error);
        }

        return ParseHistory(result.Text, path);
    }

    private static IReadOnlyList<GitCommit> ParseHistory(string output, string path)
    {
        var fields = output.Split('\0');
        var commits = new List<GitCommit>();
        for (var i = 0; i < fields.Length; i++)
        {
            if (!IsCommitIdentifier(fields[i]))
            {
                continue;
            }

            var sha = fields[i];
            var shortSha = fields[++i];
            var author = fields[++i];
            var date = DateTimeOffset.Parse(fields[++i], CultureInfo.InvariantCulture);
            var subject = fields[++i];
            commits.Add(new GitCommit(sha, shortSha, author, date, subject, path));
            path = FindEarlierPath(fields, i + 1, path);
        }

        return commits;
    }

    private static string FindEarlierPath(string[] fields, int start, string path)
    {
        // Rename status is followed by the old and new paths; earlier commits use the old name.
        for (var index = start; index < fields.Length && !IsCommitIdentifier(fields[index]); index++)
        {
            if (fields[index].TrimStart('\n', '\r').StartsWith("R", StringComparison.Ordinal) && index + 2 < fields.Length)
            {
                return fields[index + 1];
            }
        }

        return path;
    }

    private static bool IsCommitIdentifier(string value)
    {
        if (value.Length != 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Projects Git commits into provider-independent revision information.</summary>
    async Task<IReadOnlyList<VersionInfo>> IVersionControl.GetHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken)
    {
        var commits = await GetHistoryAsync(filePath, maxCount, cancellationToken).ConfigureAwait(false);
        var result = new List<VersionInfo>();
        foreach (var commit in commits)
        {
            result.Add(new VersionInfo(commit.Sha, commit.ShortSha, commit.Author, commit.Date, commit.Subject, commit.PathAtCommit));
        }

        return result;
    }

    /// <summary>Reads raw revision bytes, or null for an absent file; :0 reads the index.</summary>
    public async Task<byte[]?> ReadFileAsync(string revision, string repoRelativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("A revision is required.", nameof(revision));
        }

        if (revision != ":0")
        {
            var check = await Run(cancellationToken, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").ConfigureAwait(false);
            if (check.ExitCode != 0)
            {
                throw new GitException(check.Error);
            }

            revision = check.Text.Trim();
        }

        var spec = (revision == ":0" ? ":0:" : revision + ":") + repoRelativePath;
        var exists = await Run(cancellationToken, "cat-file", "-e", spec).ConfigureAwait(false);
        if (exists.ExitCode != 0)
        {
            if (exists.Error.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                exists.Error.IndexOf("not in the index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                exists.Error.IndexOf("not in", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            throw new GitException(exists.Error);
        }

        var result = await Run(cancellationToken, "show", spec).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new GitException(result.Error);
        }

        return result.Output;
    }
}
