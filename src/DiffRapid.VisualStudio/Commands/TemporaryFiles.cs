using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiffRapid.VisualStudio.Commands;

/// <summary>
/// The copies comparisons open (clipboard, selection, earlier versions, extracted text, plugin output): each is
/// written to its own folder %TEMP%\DiffRapid\&lt;8 hex digits&gt;, removed when its comparison closes, and swept
/// at start-up when a comparison could not remove it (Visual Studio closed or crashed, the file was locked).
/// </summary>
internal static class TemporaryFiles
{
    // Folders older than this are left from earlier sessions (an estimate: no comparison stays open that long
    // across a Visual Studio restart, and a younger folder may belong to another running instance).
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(1);

    private const int FolderNameLength = 8;

    private static string Root => Path.Combine(Path.GetTempPath(), "DiffRapid");

    /// <summary>Writes bytes as they are to a new temporary folder under a readable name and returns the path.</summary>
    public static string Write(byte[] content, string name, string extension)
    {
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N").Substring(0, FolderNameLength));
        Directory.CreateDirectory(folder);
        var safeName = UI.UiText.SafeFileName(name);
        var path = Path.Combine(folder, safeName + (safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? string.Empty : extension));
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Removes the temporary folders that hold any of <paramref name="paths"/>; paths outside %TEMP%\DiffRapid are
    /// left alone. Failures (a file still open elsewhere) are ignored: the start-up sweep retries later.
    /// </summary>
    public static void DeleteFoldersOf(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (path is not null && FolderOf(path) is { } folder)
            {
                TryDeleteFolder(folder);
            }
        }
    }

    /// <summary>Removes temporary folders older than a day; meant for a background thread at start-up.</summary>
    public static void Sweep(CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(Root);
        if (!root.Exists || IsLink(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - SweepAge;
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
            if (IsOurFolderName(folder.Name) && folder.LastWriteTimeUtc < cutoff)
            {
                TryDeleteFolder(folder.FullName);
            }
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
