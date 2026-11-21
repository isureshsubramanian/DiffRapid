using System;
using System.Collections.Concurrent;
using System.IO;

namespace DiffRapid.Core.IO;

/// <summary>
/// Finds the program a bare name such as <c>git</c> or <c>tf</c> means. Windows' CreateProcess and .NET on Unix both
/// look in the current folder before PATH, so a program planted in an opened repository could run instead; this
/// searches only the absolute folders on PATH.
/// </summary>
internal static class ExecutablePath
{
    private static readonly ConcurrentDictionary<string, string> Found = new(StringComparer.Ordinal);

    private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

    /// <summary>
    /// Returns <paramref name="executable"/> unchanged when it is a path (it has a folder part), otherwise the full path
    /// of the first match in an absolute PATH folder, with ".exe" added on Windows when the name has no extension (as
    /// CreateProcess does). Results are cached for the process's lifetime; a name that is not found is not cached.
    /// </summary>
    /// <exception cref="FileNotFoundException">No absolute PATH folder has the program.</exception>
    internal static string Resolve(string executable)
    {
        if (executable.IndexOfAny(new[] { '/', '\\' }) >= 0 || Path.IsPathRooted(executable))
        {
            return executable;
        }

        if (Found.TryGetValue(executable, out var cached))
        {
            return cached;
        }

        var found = Search(executable, Environment.GetEnvironmentVariable("PATH"))
            ?? throw new FileNotFoundException(executable + ": the program was not found in any absolute folder on PATH.", executable);
        return Found.GetOrAdd(executable, found);
    }

    /// <summary>
    /// The first match for a bare <paramref name="executable"/> in the absolute folders of <paramref name="pathVariable"/>,
    /// or null. On Unix a file without an execute bit is skipped, as the shell does, so it cannot shadow a later match.
    /// </summary>
    internal static string? Search(string executable, string? pathVariable)
    {
        var name = IsWindows && !Path.HasExtension(executable) ? executable + ".exe" : executable;
        foreach (var entry in (pathVariable ?? string.Empty).Split(Path.PathSeparator))
        {
            var folder = entry.Trim().Trim('"');
            if (!IsFullyQualified(folder))
            {
                // Empty, "." and other relative entries resolve against the current folder.
                continue;
            }

            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutable(string candidate)
    {
        try
        {
            return FileModes.Get(candidate) is not { } mode || (mode & FileModes.AnyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Path.IsPathFullyQualified is missing from netstandard2.0: "\folder" and "C:folder" are rooted but relative.
    private static bool IsFullyQualified(string folder) => IsWindows
        ? (folder.Length >= 3 && char.IsLetter(folder[0]) && folder[1] == ':' && (folder[2] == '\\' || folder[2] == '/'))
            || folder.StartsWith(@"\\", StringComparison.Ordinal)
        : folder.StartsWith("/", StringComparison.Ordinal);
}
