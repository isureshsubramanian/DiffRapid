using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiffRapid.Core.VersionControl;

/// <summary>A revision that changed a file.</summary>
/// <param name="Id">The full revision identifier.</param>
/// <param name="ShortId">The abbreviated revision identifier.</param>
/// <param name="Author">The author's display name.</param>
/// <param name="Date">The revision date.</param>
/// <param name="Subject">The revision description.</param>
/// <param name="PathAtVersion">The workspace-relative path at this revision.</param>
public sealed record VersionInfo(string Id, string ShortId, string Author, DateTimeOffset Date, string Subject, string PathAtVersion);

/// <summary>File history and content supplied by a version control workspace.</summary>
public interface IVersionControl
{
    /// <summary>The absolute local workspace root.</summary>
    string Root { get; }

    /// <summary>The provider identifier: git or tfvc.</summary>
    string Kind { get; }

    /// <summary>Lists changes newest first for a local file.</summary>
    Task<IReadOnlyList<VersionInfo>> GetHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken);

    /// <summary>Reads revision bytes, returning null when the item is absent.</summary>
    Task<byte[]?> ReadFileAsync(string versionId, string path, CancellationToken cancellationToken);
}

/// <summary>Discovers supported local workspaces.</summary>
public static class VersionControl
{
    /// <summary>Finds Git first, then a TFVC workspace.</summary>
    public static IVersionControl? Find(string path) => (IVersionControl?)Git.GitRepository.Find(path) ?? TfvcWorkspace.Find(path);
}

internal sealed record ProcessResult(int ExitCode, byte[] Output, string Error)
{
    internal string Text => Encoding.UTF8.GetString(Output);
}

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1);
            }
            else
            {
                result.Append('\\', slashes);
            }

            result.Append(character);
            slashes = 0;
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>Runs a command while capturing raw output and UTF-8 diagnostics.</summary>
    public async Task<ProcessResult> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote)))
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        StartProcess(process, executable);
        using var registration = cancellationToken.Register(() => KillProcess(process));
        return await CaptureOutputAsync(process, cancellationToken).ConfigureAwait(false);
    }

    private static void StartProcess(Process process, string executable)
    {
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException(executable + ": " + ex.Message, ex);
        }
    }

    private static void KillProcess(Process process)
    {
        // Cancellation can race with normal process exit.
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<ProcessResult> CaptureOutputAsync(Process process, CancellationToken cancellationToken)
    {
        // Drain both streams concurrently so neither pipe can block the child process.
        using var output = new MemoryStream();
        var standardOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        var standardError = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(standardOutput, standardError, Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(process.ExitCode, output.ToArray(), await standardError.ConfigureAwait(false));
    }
}
