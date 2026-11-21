using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiffRapid.VisualStudio.Commands;

/// <summary>
/// The copies comparisons open (clipboard, selection, earlier versions, extracted text, plugin output): each is
/// written to its own folder %TEMP%\DiffRapid\&lt;8 hex digits&gt;, removed when the last comparison showing it closes, and swept
/// at start-up when a comparison could not remove it (Visual Studio closed or crashed, the file was locked).
/// </summary>
internal static class TemporaryFiles
{
    // Folders older than this are left from earlier sessions (an estimate: no comparison stays open that long
    // across a Visual Studio restart, and a younger folder may belong to another running instance).
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(1);

    // Each folder gets an empty marker file ".owner-<process id>" when it is written, so the sweep of another
    // Visual Studio instance leaves it alone while this one runs, however long the comparison stays open. The
    // editor does not keep the copy open, so a locked-file test alone cannot tell. A reused process id only keeps
    // a folder longer, never deletes one in use.
    private const string OwnerMarkerPrefix = ".owner-";

    private const int FolderNameLength = 8;

    private static string Root => Path.Combine(Path.GetTempPath(), "DiffRapid");

    /// <summary>Writes bytes as they are to a new temporary folder under a readable name and returns the path.</summary>
    public static string Write(byte[] content, string name, string extension)
    {
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N").Substring(0, FolderNameLength));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, OwnerMarkerPrefix + CurrentProcessId.ToString(CultureInfo.InvariantCulture)), Array.Empty<byte>());
        var safeName = UI.UiText.SafeFileName(name);
        var path = Path.Combine(folder, safeName + (safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? string.Empty : extension));
        File.WriteAllBytes(path, content);
        return path;
    }

    // How many open comparison tabs show a file in each temporary folder: one tab can open another on the same copy
    // ("Open as text" from a table, a structure comparison), so a folder is deleted only when the last one closes.
    // Guarded by HoldsLock; a deletion re-checks under the lock, so a folder taken again before it runs is kept.
    private static readonly Dictionary<string, int> Holds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object HoldsLock = new();

    /// <summary>
    /// Takes one hold on the temporary folders of <paramref name="paths"/> for a comparison tab; paths outside
    /// %TEMP%\DiffRapid are ignored. Each call is matched by one <see cref="Release"/> with the same paths.
    /// </summary>
    public static void Acquire(IEnumerable<string?> paths)
    {
        var folders = FoldersOf(paths);
        lock (HoldsLock)
        {
            foreach (var folder in folders)
            {
                Holds[folder] = Holds.TryGetValue(folder, out var count) ? count + 1 : 1;
            }
        }
    }

    /// <summary>Drops one hold taken by <see cref="Acquire"/> and deletes, in the background, the folders no tab holds any more.</summary>
    public static void Release(IEnumerable<string?> paths)
    {
        var folders = FoldersOf(paths);
        lock (HoldsLock)
        {
            foreach (var folder in folders)
            {
                if (Holds.TryGetValue(folder, out var count))
                {
                    if (count > 1)
                    {
                        Holds[folder] = count - 1;
                    }
                    else
                    {
                        Holds.Remove(folder);
                    }
                }
            }
        }

        DeleteUnheld(folders);
    }

    /// <summary>
    /// Deletes, in the background, the temporary folders of <paramref name="paths"/> that no comparison tab holds:
    /// copies made for one view only, or written for a view that failed to open. Failures (a file still open
    /// elsewhere) are ignored: the start-up sweep retries later.
    /// </summary>
    public static void DeleteUnused(IEnumerable<string?> paths) => DeleteUnheld(FoldersOf(paths));

    private static void DeleteUnheld(List<string> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var folder in folders)
            {
                lock (HoldsLock)
                {
                    if (!Holds.ContainsKey(folder))
                    {
                        TryDeleteFolder(folder);
                    }
                }
            }
        });
    }

    private static List<string> FoldersOf(IEnumerable<string?> paths) =>
        paths.Select(path => path is null ? null : FolderOf(path)).OfType<string>().ToList();

    /// <summary>
    /// Removes temporary folders older than a day that no running Visual Studio still uses (see
    /// <see cref="OwnerMarkerPrefix"/>) and whose files nothing has open; meant for a background thread at start-up.
    /// </summary>
    public static void Sweep(CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(Root);
        if (!root.Exists || IsLink(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - SweepAge;
        string processName;
        using (var current = Process.GetCurrentProcess())
        {
            processName = current.ProcessName;
        }

        IEnumerable<DirectoryInfo> folders;
        try
        {
            folders = root.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOurFolderName(folder.Name) && folder.LastWriteTimeUtc < cutoff && !InUse(folder, processName))
            {
                TryDeleteFolder(folder.FullName);
            }
        }
    }

    private static int CurrentProcessId
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return current.Id;
        }
    }

    // In use when its owner (a process of the same name as this one) still runs, or when one of its files cannot be
    // opened exclusively. Anything that cannot be checked counts as in use: the folder is then left for a later sweep.
    private static bool InUse(DirectoryInfo folder, string processName)
    {
        try
        {
            foreach (var file in folder.EnumerateFiles())
            {
                if (file.Name.StartsWith(OwnerMarkerPrefix, StringComparison.Ordinal))
                {
                    if (int.TryParse(file.Name.Substring(OwnerMarkerPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                        && IsRunning(id, processName))
                    {
                        return true;
                    }
                }
                else if (!IsLink(file))
                {
                    // A link is never opened: that would test what it points to.
                    using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool IsRunning(int processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No such process, or it has exited.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Running, but not ours to inspect.
            return true;
        }
    }

    // The folder directly under %TEMP%\DiffRapid that holds the path, or null when the path is not such a copy.
    private static string? FolderOf(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            return parent is not null
                && string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && IsOurFolderName(Path.GetFileName(folder))
                ? folder
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsOurFolderName(string? name) =>
        name is { Length: FolderNameLength } && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    // Only the files directly in the folder are deleted, then the folder if it is empty: these folders never hold
    // subfolders, so anything else (and any link) is not ours and is left in place. A link file is removed itself,
    // never what it points to.
    private static void TryDeleteFolder(string path)
    {
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists || IsLink(folder))
            {
                return;
            }

            foreach (var file in folder.EnumerateFiles())
            {
                file.Delete();
            }

            folder.Delete(recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Still open somewhere, or not ours to delete; the start-up sweep tries again in a later session.
        }
    }
}
