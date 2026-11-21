using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Core.IO;

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

/// <summary>
/// Runs a program directly (never through a shell) and captures its output. The one place that starts child
/// processes for version control and external command plugins, so all of them get the same limits: standard input is
/// redirected (and closed after any input is written, so a prompt cannot hang), standard output is capped, and a
/// cancelled, timed-out or over-limit program is killed and waited for before this returns.
/// </summary>
/// <remarks>
/// Only the direct child is killed: netstandard2.0 has no Process.Kill(entireProcessTree), so a grandchild the
/// program started may keep running (and may keep the output pipes open, which <see cref="DrainTimeout"/> bounds).
/// </remarks>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// Estimate: larger output is not a file revision or a plugin's text but a runaway program; 256 MB keeps the
    /// capture and its one copy (512 MB at worst) affordable inside the Visual Studio process.
    /// </summary>
    internal const long DefaultOutputLimit = 256L * 1024 * 1024;

    // Estimate: diagnostics past 64 KB add nothing to an error message; the rest is read and dropped.
    private const int ErrorLimit = 64 * 1024;

    // Estimate: an exited program's pipes close at once; this only bounds a grandchild that still holds them.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

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
    public Task<ProcessResult> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => RunAsync(executable, directory, arguments, null, DefaultOutputLimit, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="standardInput"/> (none when null) and returns its exit
    /// code, output and diagnostics. Worst case: <paramref name="outputLimit"/> bytes of output plus one copy, and
    /// 64 KB of diagnostics, for at most <paramref name="timeout"/> plus the drain time.
    /// </summary>
    /// <exception cref="IOException">The program could not start (a bare name is looked up only in absolute PATH
    /// folders, never the current one), wrote more than <paramref name="outputLimit"/> bytes,
    /// or left its output open after exiting.</exception>
    /// <exception cref="TimeoutException">The program ran past <paramref name="timeout"/> and was stopped.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the program was stopped.</exception>
    internal static async Task<ProcessResult> RunAsync(string executable, string? directory, IReadOnlyList<string> arguments,
        byte[]? standardInput, long outputLimit, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExecutablePath.Resolve(executable), string.Join(" ", arguments.Select(Quote)))
            {
                WorkingDirectory = directory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        StartProcess(process, executable);
        var limitExceeded = false;
        // Set only when the deadline stops a running program, and before it is stopped, so the exit it causes can
        // never be read first: a deadline that passes after a normal exit, while the output drains, is no timeout.
        var stoppedAtDeadline = false;
        using var deadline = new CancellationTokenSource(Bounded(timeout));
        using var cancelled = cancellationToken.Register(() => KillProcess(process));
        using var timedOut = deadline.Token.Register(() => KillProcess(process, () => stoppedAtDeadline = true));
        _ = WriteInputAsync(process.StandardInput.BaseStream, standardInput ?? Array.Empty<byte>());

        // Drain both streams concurrently so neither pipe can block the child process.
        var output = ReadAsync(process.StandardOutput.BaseStream, outputLimit, () =>
        {
            limitExceeded = true;
            KillProcess(process);
        });
        var error = ReadAsync(process.StandardError.BaseStream, ErrorLimit, null);
        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
        var streams = Task.WhenAll(output, error);
        var drained = await Task.WhenAny(streams, Task.Delay(DrainTimeout)).ConfigureAwait(false) == streams;

        // Disposing waits for a deadline callback that is still running, so stoppedAtDeadline is final below.
        timedOut.Dispose();

        cancellationToken.ThrowIfCancellationRequested();
        if (limitExceeded)
        {
            throw new IOException($"{executable}: the output passed {outputLimit / (1024 * 1024)} MB and the program was stopped.");
        }

        if (stoppedAtDeadline)
        {
            throw new TimeoutException($"{executable} did not finish within {timeout.TotalSeconds:0.###} seconds and was stopped.");
        }

        if (!drained)
        {
            throw new IOException(executable + ": the output did not finish after the program exited.");
        }

        return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), Encoding.UTF8.GetString(await error.ConfigureAwait(false)));
    }

    // CancellationTokenSource takes at most int.MaxValue milliseconds; anything longer (or infinite) means no limit.
    private static TimeSpan Bounded(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds >= int.MaxValue ? Timeout.InfiniteTimeSpan
        : timeout < TimeSpan.Zero ? TimeSpan.Zero
        : timeout;

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

    /// <summary>Stops the program if it is still running, calling <paramref name="stopping"/> first.</summary>
    private static void KillProcess(Process process, Action? stopping = null)
    {
        // Cancellation can race with normal process exit.
        try
        {
            if (!process.HasExited)
            {
                stopping?.Invoke();
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

    /// <summary>
    /// Reads a stream to its end, keeping at most <paramref name="limit"/> bytes. Past the limit,
    /// <paramref name="exceeded"/> runs and reading stops; without it, the rest is read and dropped.
    /// </summary>
    private static async Task<byte[]> ReadAsync(Stream stream, long limit, Action? exceeded)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
        {
            var room = limit - buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, (int)Math.Max(0, room));
                if (exceeded is not null)
                {
                    exceeded();
                    break;
                }

                continue;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task WriteInputAsync(Stream stream, byte[] input)
    {
        // A program may exit without reading all of its input; that is not an error.
        try
        {
            await stream.WriteAsync(input, 0, input.Length).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
