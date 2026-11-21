using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Core.IO;
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
        var relative = PathContainment.RelativeInside(Root, Path.GetFullPath(fullPath))
            ?? throw new ArgumentException("The file is outside the repository.", nameof(fullPath));
        return relative.Replace('\\', '/');
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
            "log", "--follow", "-z", "--name-status", "--format=" + RecordMarker + "%H%x00%h%x00%an%x00%aI%x00%s",
            "-n", maxCount.ToString(CultureInfo.InvariantCulture), "--", path).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new GitException(result.Error);
        }

        return ParseHistory(result.Text, path);
    }

    // Each commit record starts with this byte. Fields are found by position, so a file whose name looks like a
    // commit identifier (or starts with this byte) is never read as a commit.
    private const string RecordMarker = "%x01";

    /// <summary>
    /// Parses "log -z --name-status" output: per commit, the marker and five NUL-separated fields, then status
    /// entries, each a status followed by one path (two for renames and copies, old then new).
    /// </summary>
    /// <exception cref="GitException">The output does not have that shape.</exception>
    internal static IReadOnlyList<GitCommit> ParseHistory(string output, string path)
    {
        var fields = output.Split('\0');
        var commits = new List<GitCommit>();
        var i = 0;
        while (i < fields.Length)
        {
            var field = fields[i];
            if (field.Length == 0 && i == fields.Length - 1)
            {
                break;
            }

            if (field.Length == 0 || field[0] != '\x01' || i + 4 >= fields.Length || !IsCommitIdentifier(field.Substring(1))
                || !DateTimeOffset.TryParse(fields[i + 3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new GitException("unexpected log output.");
            }

            commits.Add(new GitCommit(field.Substring(1), fields[i + 1], fields[i + 2], date, fields[i + 4], path));
            i += 5;
            var earlier = path;
            while (i < fields.Length && !(fields[i].Length > 0 && fields[i][0] == '\x01'))
            {
                var status = fields[i].TrimStart('\n', '\r');
                if (status.Length == 0)
                {
                    i++;
                    continue;
                }

                var paths = status[0] is 'R' or 'C' ? 2 : 1;
                if (i + paths >= fields.Length)
                {
                    throw new GitException("unexpected log output.");
                }

                // Rename status is followed by the old and new paths; earlier commits use the old name.
                if (status[0] == 'R')
                {
                    earlier = fields[i + 1];
                }

                i += paths + 1;
            }

            path = earlier;
        }

        return commits;
    }

    // SHA-1 (40) or SHA-256 (64) object names in hexadecimal.
    private static bool IsCommitIdentifier(string value)
    {
        if (value.Length != 40 && value.Length != 64)
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

        // One read: a missing path is told apart by git's two messages for it, "path 'x' does not exist in 'rev'"
        // (or "(neither on disk nor in the index)") and "path 'x' exists on disk, but not in 'rev'" (or "the index").
        var spec = (revision == ":0" ? ":0:" : revision + ":") + repoRelativePath;
        var result = await Run(cancellationToken, "cat-file", "blob", spec).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.Error.IndexOf("' does not exist", StringComparison.Ordinal) >= 0 ||
                result.Error.IndexOf("' exists on disk, but not in ", StringComparison.Ordinal) >= 0)
            {
                return null;
            }

            throw new GitException(result.Error);
        }

        return result.Output;
    }
}
