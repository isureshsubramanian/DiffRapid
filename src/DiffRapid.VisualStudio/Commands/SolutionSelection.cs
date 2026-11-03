using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DiffRapid.VisualStudio.Commands;

/// <summary>Reads the file paths selected in Solution Explorer (solution or folder view).</summary>
internal static class SolutionSelection
{
    public static IReadOnlyList<string> GetSelectedFilePaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<string>();
        if (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection monitor)
        {
            return result;
        }

        var hierarchyPtr = IntPtr.Zero;
        var containerPtr = IntPtr.Zero;
        try
        {
            if (ErrorHandler.Failed(monitor.GetCurrentSelection(out hierarchyPtr, out var itemId, out var multiSelect, out containerPtr)))
            {
                return result;
            }

            if (multiSelect != null)
            {
                ErrorHandler.ThrowOnFailure(multiSelect.GetSelectionInfo(out var count, out _));
                var items = new VSITEMSELECTION[count];
                ErrorHandler.ThrowOnFailure(multiSelect.GetSelectedItems(0, count, items));
                foreach (var item in items)
                {
                    AddPath(result, item.pHier, item.itemid);
                }
            }
            else if (hierarchyPtr != IntPtr.Zero)
            {
                var hierarchy = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierarchyPtr);
                AddPath(result, hierarchy, itemId);
            }
        }
        finally
        {
            if (hierarchyPtr != IntPtr.Zero)
            {
                Marshal.Release(hierarchyPtr);
            }

            if (containerPtr != IntPtr.Zero)
            {
                Marshal.Release(containerPtr);
            }
        }

        return result;
    }

    private static void AddPath(List<string> paths, IVsHierarchy? hierarchy, uint itemId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (hierarchy is null || ErrorHandler.Failed(hierarchy.GetCanonicalName(itemId, out var name)))
        {
            return;
        }

        if (!string.IsNullOrEmpty(name) && File.Exists(name))
        {
            paths.Add(name);
        }
    }
}
