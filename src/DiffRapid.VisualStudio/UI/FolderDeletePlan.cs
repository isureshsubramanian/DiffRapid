using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DiffRapid.Core.Folders;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Selected folder comparison entries to remove from one side. A selected folder goes with everything in
/// it; entries inside a selected folder are not listed again. Items go to the Recycle Bin where the drive has one
/// (see <see cref="RecycleBin"/>).
/// </summary>
internal sealed class FolderDeletePlan
{
    private readonly List<FolderCompareItem> _entries = new();

    private FolderDeletePlan()
    {
    }

    public int Files => _entries.Count(e => e.Kind != FolderItemKind.Folder);

    public int Folders => _entries.Count(e => e.Kind == FolderItemKind.Folder);

    public bool IsEmpty => _entries.Count == 0;

    public static FolderDeletePlan Create(IEnumerable<FolderCompareItem> selected, bool fromLeft)
    {
        var plan = new FolderDeletePlan();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = selected
            .Where(i => i.RelativePath.Length > 0 && (fromLeft ? i.Left : i.Right).Exists)
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in candidates)
        {
            // Sorted, so a containing folder is always seen before what is inside it; only the item's own parent
            // folders are looked up, rather than every entry so far.
            if (!InsideAny(item.RelativePath, folders))
            {
                plan._entries.Add(item);
                if (item.Kind == FolderItemKind.Folder)
                {
                    folders.Add(item.RelativePath);
                }
            }
        }

        return plan;
    }

    private static bool InsideAny(string relativePath, HashSet<string> folders)
    {
        for (var slash = relativePath.IndexOf('/'); slash > 0; slash = relativePath.IndexOf('/', slash + 1))
        {
            if (folders.Contains(relativePath.Substring(0, slash)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The side every entry is on, when each exists on one side only; null for a mixed selection.</summary>
    public static bool? OnlySide(IReadOnlyCollection<FolderCompareItem> selected)
    {
        if (selected.Count == 0)
        {
            return null;
        }

        if (selected.All(i => i.Left.Exists && !i.Right.Exists))
        {
            return true;
        }

        return selected.All(i => i.Right.Exists && !i.Left.Exists) ? false : null;
    }

    /// <summary>Deletes the entries (see <see cref="RecycleBin.Delete"/>); failures are collected per entry rather than stopping.</summary>
    public IReadOnlyList<string> Run(string root, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                RecycleBin.Delete(path, entry.Kind == FolderItemKind.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                errors.Add($"{entry.RelativePath}: {ex.Message}");
            }
        }

        return errors;
    }
}
