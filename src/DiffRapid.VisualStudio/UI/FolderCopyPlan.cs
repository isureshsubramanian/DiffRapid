using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DiffRapid.Core.Folders;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// What copying selected folder comparison entries to the other side would do: the files that differ or
/// exist only on the source side, and the folders that must be created. Identical, excluded and unreadable
/// entries and links are left alone; a selected folder stands for everything in it.
/// </summary>
internal sealed class FolderCopyPlan
{
    private readonly List<string> _folders = new();
    private readonly List<string> _files = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _toRight;

    private FolderCopyPlan(bool toRight)
    {
        _toRight = toRight;
    }

    /// <summary>Relative paths of the files to copy.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>Files that already exist on the target side and will be replaced.</summary>
    public int Overwrites { get; private set; }

    public bool IsEmpty => _files.Count == 0 && _folders.Count == 0;

    public static FolderCopyPlan Create(IEnumerable<FolderCompareItem> selected, bool toRight)
    {
        var plan = new FolderCopyPlan(toRight);
        foreach (var item in selected)
        {
            plan.Add(item);
        }

        return plan;
    }

    /// <summary>Copies the planned entries; failures are collected per file rather than stopping the copy.</summary>
    public IReadOnlyList<string> Run(string sourceRoot, string targetRoot, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var folder in _folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Combine(targetRoot, folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder}: {ex.Message}");
            }
        }

        foreach (var file in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Combine(targetRoot, file);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Combine(sourceRoot, file), target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        return errors;
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Add(FolderCompareItem item)
    {
        var source = _toRight ? item.Left : item.Right;
        var target = _toRight ? item.Right : item.Left;
        if (!source.Exists || item.Status is FolderCompareStatus.Skipped or FolderCompareStatus.TypeConflict)
        {
            return;
        }

        switch (item.Kind)
        {
            case FolderItemKind.Folder:
                if (!target.Exists && item.RelativePath.Length > 0 && _seen.Add(item.RelativePath))
                {
                    _folders.Add(item.RelativePath);
                }

                foreach (var child in item.Children)
                {
                    Add(child);
                }

                break;

            case FolderItemKind.File when item.Status is not (FolderCompareStatus.Identical or FolderCompareStatus.Error):
                if (_seen.Add(item.RelativePath))
                {
                    _files.Add(item.RelativePath);
                    if (target.Exists)
                    {
                        Overwrites++;
                    }
                }

                break;
        }
    }
}
