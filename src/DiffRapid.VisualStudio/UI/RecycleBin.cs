using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// The one place the folder views delete files: to the Recycle Bin where the drive has one, permanently where it
/// does not. Callers confirm a permanent delete with the user first (see <see cref="CanRecycle"/>).
/// </summary>
internal static class RecycleBin
{
    /// <summary>
    /// Whether deleting under <paramref name="path"/> can go to the Recycle Bin. Only local fixed drives count:
    /// network shares (UNC paths and mapped drives), removable and optical drives and anything that cannot be
    /// identified are treated as having none, because the shell deletes there for good without asking.
    /// Large items can still exceed the Recycle Bin's size limit on a fixed drive; that is not checked here.
    /// </summary>
    public static bool CanRecycle(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a file or a folder with everything in it: to the Recycle Bin when <see cref="CanRecycle"/> says the drive
    /// has one, otherwise permanently. The shell shows its own error dialogs only.
    /// </summary>
    public static void Delete(string path, bool isFolder)
    {
        var recycle = CanRecycle(path) ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        if (isFolder)
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
        }
        else
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
        }
    }
}
