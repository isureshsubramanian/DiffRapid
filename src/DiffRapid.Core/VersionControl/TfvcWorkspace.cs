using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiffRapid.Core.VersionControl;

/// <summary>Reads TFVC workspace revisions through tf.exe.</summary>
public sealed class TfvcWorkspace : IVersionControl
{
    private readonly string _executable;
    private readonly IProcessRunner _runner;

    /// <summary>Optional executable override; otherwise vswhere and PATH are searched.</summary>
    public static string? TfExecutablePath { get; set; }

    /// <summary>The absolute local workspace root.</summary>
    public string Root { get; }

    /// <summary>The TFVC provider identifier.</summary>
    public string Kind => "tfvc";

    internal TfvcWorkspace(string root, string executable, IProcessRunner runner)
    {
        Root = Path.GetFullPath(root);
        _executable = executable;
        _runner = runner;
    }

    private static string Locate(IProcessRunner runner)
    {
        var installer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        return Locate(runner, installer, TfExecutablePath);
    }

    internal static string Locate(IProcessRunner runner, string installer, string? executableOverride)
    {
        if (!string.IsNullOrEmpty(executableOverride))
        {
            return executableOverride!;
        }

        if (File.Exists(installer))
        {
            return LocateWithVisualStudioInstaller(installer, runner);
        }

        return "tf";
    }

    private static string LocateWithVisualStudioInstaller(string installer, IProcessRunner runner)
    {
        // Prefer the first tf.exe from the latest installation that includes Team Explorer.
        var result = runner.RunAsync(installer, Environment.CurrentDirectory,
            new[] { "-latest", "-requires", "Microsoft.VisualStudio.TeamExplorer", "-find", "**\\tf.exe" },
            CancellationToken.None).GetAwaiter().GetResult();
        var candidates = result.Text.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return result.ExitCode == 0 && candidates.Length > 0 ? candidates[0] : "tf";
    }

    /// <summary>Finds workspace metadata or queries local workfold mappings.</summary>
    public static TfvcWorkspace? Find(string path)
    {
        var runner = new ProcessRunner();
        return Find(path, Locate(runner), runner);
    }

    internal static TfvcWorkspace? Find(string path, string executable, IProcessRunner runner)
    {
        var full = Path.GetFullPath(path);
        var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        for (var current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, "$tf")) || Directory.Exists(Path.Combine(current, ".tf")))
            {
                return new TfvcWorkspace(current, executable, runner);
            }
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = runner.RunAsync(executable, directory, new[] { "vc", "workfold", full, "/noprompt" }, timeout.Token).GetAwaiter().GetResult();
            var root = result.ExitCode == 0 ? ParseWorkfold(result.Text, full) : null;
            return root == null ? null : new TfvcWorkspace(root, executable, runner);
        }
        catch (IOException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // tf.exe emits localized labels; these parsers accept English detailed output.
    internal static string? ParseWorkfold(string output, string path)
    {
        string? best = null;
        var full = Path.GetFullPath(path);
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*\$/.*?:\s*(.+?)\s*$");
            if (!match.Success)
            {
                continue;
            }

            var local = match.Groups[1].Value.TrimEnd('\\', '/');
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if ((full.Equals(local, comparison) || full.StartsWith(local + Path.DirectorySeparatorChar, comparison)) &&
                (best == null || local.Length > best.Length))
            {
                best = local;
            }
        }

        return best;
    }

    internal static IReadOnlyList<VersionInfo> ParseHistory(string output, string path)
    {
        var results = new List<VersionInfo>();
        var blocks = Regex.Split(output, @"(?m)^Changeset:\s*");
        for (var i = 1; i < blocks.Length; i++)
        {
            results.Add(ParseChangeset(blocks[i], path));
        }

        if (results.Count == 0 && output.IndexOf("Changeset", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new IOException("tf: Unsupported history format; English detailed output is required.");
        }

        return results;
    }

    private static VersionInfo ParseChangeset(string block, string path)
    {
        var id = Regex.Match(block, @"^\d+").Value;
        var author = Regex.Match(block, @"(?m)^User:\s*(.*)").Groups[1].Value.Trim();
        var dateText = Regex.Match(block, @"(?m)^Date:\s*(.*)").Groups[1].Value.Trim();
        if (id.Length == 0 || !DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            throw new IOException("tf: Unsupported history date or changeset format.");
        }

        // Comments may span lines and end at any of the detailed output sections.
        var comment = Regex.Match(block, @"(?ms)^Comment:\s*\r?\n(.*?)(?=^Items:|^Check-in Notes:|^-{3,}|\z)").Groups[1].Value.Trim();
        return new VersionInfo("C" + id, "C" + id, author, date, comment, path);
    }

    private string LocalPath(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The file is outside the TFVC workspace.", nameof(path));
        }

        return full;
    }

    /// <summary>Lists changes using English detailed tf.exe output and invariant date parsing.</summary>
    public async Task<IReadOnlyList<VersionInfo>> GetHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        if (maxCount == 0)
        {
            return Array.Empty<VersionInfo>();
        }

        var full = LocalPath(filePath);
        var result = await _runner.RunAsync(_executable, Root,
            new[] { "vc", "history", full, "/noprompt", "/format:detailed", "/stopafter:" + maxCount.ToString(CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException("tf: " + result.Error.Trim());
        }

        return ParseHistory(result.Text, full.Substring(Root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'));
    }

    /// <summary>Reads a changeset, latest server version T, or fetched workspace version W.</summary>
    public async Task<byte[]?> ReadFileAsync(string versionId, string path, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(versionId, @"^(C[0-9]+|T|W)$"))
        {
            throw new ArgumentException("tf: Expected a changeset C123, T, or W.", nameof(versionId));
        }

        var temp = Path.GetTempFileName();
        try
        {
            var result = await _runner.RunAsync(_executable, Root,
                new[] { "vc", "view", LocalPath(path), "/version:" + versionId, "/noprompt", "/output:" + temp },
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (Regex.IsMatch(result.Error + result.Text, @"(?i)(TF14098|TF10128|does not exist|No items match)"))
                {
                    return null;
                }

                throw new IOException("tf: " + result.Error.Trim() + " " + result.Text.Trim());
            }

            return File.ReadAllBytes(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
