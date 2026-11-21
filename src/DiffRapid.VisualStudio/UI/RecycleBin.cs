using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// The one place the folder views delete files. Callers ask <see cref="CanRecycle"/> first and confirm a permanent
/// delete with the user where it says no. A delete meant for the Recycle Bin still has the shell warn before anything
/// would be deleted for good, for the cases this check cannot see (an item over the bin's size limit, a drive with
/// the bin turned off, a substituted drive).
/// </summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;

    // Asks before an item that cannot go to the Recycle Bin is deleted for good, even with FOF_NOCONFIRMATION.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Whether deleting under <paramref name="path"/> can go to the Recycle Bin. Only local fixed drives count:
    /// network shares (UNC paths and mapped drives), removable and optical drives and anything that cannot be
    /// identified are treated as having none, and so is a path through a junction, symbolic link or folder-mounted
    /// volume, which can lead to any of those.
    /// </summary>
    public static bool CanRecycle(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                return false;
            }

            // Attributes are read without following the entry itself, so a link counts wherever it points.
            for (var current = fullPath; current is not null && current.Length > root.Length; current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The window the shell's questions belong to; read on the UI thread, before the delete moves to a background one.</summary>
    public static IntPtr OwnerWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out var window))
            ? window
            : IntPtr.Zero;
    }

    /// <summary>
    /// Deletes a file or a folder with everything in it. Unless <paramref name="permanent"/> (the user has already
    /// agreed to a permanent delete), it goes to the Recycle Bin and the shell asks before deleting anything for good.
    /// The shell shows its own error dialogs only.
    /// </summary>
    /// <param name="owner">From <see cref="OwnerWindow"/>.</param>
    /// <exception cref="IOException">The shell could not delete the item, or the user declined a permanent delete.</exception>
    public static void Delete(string path, IntPtr owner, bool permanent)
    {
        var operation = new ShFileOperation
        {
            Window = owner,
            Function = FO_DELETE,

            // A list of paths, each ended by a NUL, with one more NUL after the last: marshalling adds that one.
            From = Path.GetFullPath(path) + "\0",
            Flags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | (permanent ? 0 : FOF_ALLOWUNDO | FOF_WANTNUKEWARNING)),
        };

        var result = SHFileOperation(ref operation);
        if (operation.AnyOperationsAborted)
        {
            throw new IOException("The delete was cancelled.");
        }

        if (result != 0)
        {
            throw new IOException($"The shell could not delete the item (error 0x{result:X}).");
        }
    }

    // SHFILEOPSTRUCTW with natural alignment: Visual Studio 2022 and later run only as 64-bit processes (x64, Arm64).
    // A 32-bit process would need Pack = 1, as shellapi.h declares there.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation(ref ShFileOperation operation);
}
