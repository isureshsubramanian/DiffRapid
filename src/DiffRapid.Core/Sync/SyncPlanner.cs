using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Sync;

/// <summary>
/// Turns a folder comparison into a list of sync operations. Planning only reads the comparison result; nothing
/// on disk is touched until <see cref="SyncExecutor.Execute"/> runs the plan.
/// </summary>
public static class SyncPlanner
{
    /// <summary>Plans a sync of the two compared folders under the given profile.</summary>
    /// <exception cref="ArgumentException">The comparison itself failed.</exception>
    public static SyncPlan Plan(FolderCompareResult comparison, SyncProfile? profile = null, CancellationToken cancellationToken = default)
    {
        if (comparison is null)
        {
            throw new ArgumentNullException(nameof(comparison));
        }

        profile ??= new SyncProfile();
        if (!Enum.IsDefined(typeof(SyncMode), profile.Mode) || profile.TimeTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (comparison.Root.Status == FolderCompareStatus.Error)
        {
            throw new ArgumentException("The folder comparison failed: " + comparison.Root.Error, nameof(comparison));
        }

        var walk = new Walk(comparison, profile, cancellationToken);
        foreach (var child in comparison.Root.Children)
        {
            walk.Visit(child);
        }

        return new SyncPlan(comparison.LeftRoot, comparison.RightRoot, profile, walk.Operations.AsReadOnly());
    }

    private sealed class Walk
    {
        private readonly FolderCompareResult _comparison;
        private readonly SyncProfile _profile;
        private readonly CancellationToken _token;

        public Walk(FolderCompareResult comparison, SyncProfile profile, CancellationToken token)
        {
            _comparison = comparison;
            _profile = profile;
            _token = token;
        }

        public List<SyncOperation> Operations { get; } = new();

        public void Visit(FolderCompareItem item)
        {
            _token.ThrowIfCancellationRequested();
            var skipReason = SkipReason(item);
            if (skipReason is not null)
            {
                Add(SyncAction.Skip, SyncDirection.None, item, skipReason);
                return;
            }

            switch (item.Status)
            {
                case FolderCompareStatus.Identical:
                    return;
                case FolderCompareStatus.TypeConflict:
                    VisitTypeConflict(item);
                    return;
                case FolderCompareStatus.LeftOnly:
                    VisitOneSided(item, leftExists: true);
                    return;
                case FolderCompareStatus.RightOnly:
                    VisitOneSided(item, leftExists: false);
                    return;
                case FolderCompareStatus.Different when item.Kind == FolderItemKind.Folder:
                    VisitChildren(item);
                    return;
                case FolderCompareStatus.Different:
                    VisitDifferentFile(item);
                    return;
            }
        }

        private string? SkipReason(FolderCompareItem item)
        {
            if (item.Status == FolderCompareStatus.Skipped)
            {
                return "Excluded by the comparison filters.";
            }

            if (item.Status == FolderCompareStatus.Error)
            {
                return item.Error ?? "The entry could not be compared.";
            }

            if (IsExcludedByProfile(item))
            {
                return "Excluded by the sync filter.";
            }

            if (IsLink(item.Left) || IsLink(item.Right))
            {
                return "Links are not followed.";
            }

            return null;
        }

        private bool IsExcludedByProfile(FolderCompareItem item)
        {
            if (_profile.Filter is not { } filter)
            {
                return false;
            }

            if (item.Kind == FolderItemKind.Folder)
            {
                return !filter.IsFolderIncluded(item.RelativePath);
            }

            var name = item.RelativePath.Substring(item.RelativePath.LastIndexOf('/') + 1);
            return !filter.IsFileIncluded(name);
        }

        private static bool IsLink(FileMeta meta) => meta.Exists && (meta.Attributes & FileAttributes.ReparsePoint) != 0;

        private void VisitTypeConflict(FolderCompareItem item)
        {
            const string reason = "A file on one side has the same name as a folder on the other.";
            var action = _profile.Mode == SyncMode.UpdateBoth ? SyncAction.Conflict : SyncAction.Skip;
            Add(action, SyncDirection.None, item, reason);
        }

        private void VisitOneSided(FolderCompareItem item, bool leftExists)
        {
            var direction = leftExists ? SyncDirection.LeftToRight : SyncDirection.RightToLeft;
            switch (_profile.Mode)
            {
                case SyncMode.MirrorLeftToRight:
                    CopyOrDelete(item, copy: leftExists, SyncDirection.LeftToRight);
                    return;
                case SyncMode.MirrorRightToLeft:
                    CopyOrDelete(item, copy: !leftExists, SyncDirection.RightToLeft);
                    return;
                case SyncMode.Update when !leftExists:
                    Add(SyncAction.Skip, SyncDirection.None, item, "Only on the right; Update never deletes.");
                    return;
                default:
                    Copy(item, direction);
                    return;
            }
        }

        private void CopyOrDelete(FolderCompareItem item, bool copy, SyncDirection direction)
        {
            if (copy)
            {
                Copy(item, direction);
            }
            else
            {
                Delete(item, direction);
            }
        }

        private void Copy(FolderCompareItem item, SyncDirection direction)
        {
            if (item.Kind == FolderItemKind.Folder)
            {
                Add(SyncAction.CreateFolder, direction, item, null);
                VisitChildren(item);
            }
            else
            {
                Add(SyncAction.Copy, direction, item, null);
            }
        }

        // Folder contents are deleted one by one before the folder, so excluded entries inside it survive and the
        // folder itself is kept.
        private void Delete(FolderCompareItem item, SyncDirection direction)
        {
            if (item.Kind != FolderItemKind.Folder)
            {
                Add(SyncAction.Delete, direction, item, null);
                return;
            }

            var firstChild = Operations.Count;
            VisitChildren(item);
            for (var i = firstChild; i < Operations.Count; i++)
            {
                if (Operations[i].Action != SyncAction.Delete)
                {
                    Add(SyncAction.Skip, SyncDirection.None, item, "The folder keeps entries that are not deleted.");
                    return;
                }
            }

            Add(SyncAction.Delete, direction, item, null);
        }

        private void VisitChildren(FolderCompareItem item)
        {
            foreach (var child in item.Children)
            {
                Visit(child);
            }
        }

        private void VisitDifferentFile(FolderCompareItem item)
        {
            switch (_profile.Mode)
            {
                case SyncMode.MirrorLeftToRight:
                    Add(SyncAction.Overwrite, SyncDirection.LeftToRight, item, null);
                    return;
                case SyncMode.MirrorRightToLeft:
                    Add(SyncAction.Overwrite, SyncDirection.RightToLeft, item, null);
                    return;
                case SyncMode.Update:
                    VisitUpdate(item);
                    return;
                default:
                    VisitUpdateBoth(item);
                    return;
            }
        }

        private void VisitUpdate(FolderCompareItem item)
        {
            var newer = Newer(item);
            if (newer == SyncDirection.LeftToRight)
            {
                Add(SyncAction.Overwrite, SyncDirection.LeftToRight, item, null);
            }
            else if (newer == SyncDirection.RightToLeft)
            {
                Add(SyncAction.Skip, SyncDirection.None, item, "The right file is newer.");
            }
            else
            {
                Add(SyncAction.Skip, SyncDirection.None, item, "Both files have the same time but different contents.");
            }
        }

        private void VisitUpdateBoth(FolderCompareItem item)
        {
            if (_profile.LastSyncUtc is { } lastSync
                && item.Left.LastWriteUtc - lastSync > _profile.TimeTolerance
                && item.Right.LastWriteUtc - lastSync > _profile.TimeTolerance)
            {
                Add(SyncAction.Conflict, SyncDirection.None, item, "Changed on both sides since the last sync.");
                return;
            }

            var newer = Newer(item);
            if (newer == SyncDirection.None)
            {
                Add(SyncAction.Conflict, SyncDirection.None, item, "Both files have the same time but different contents.");
                return;
            }

            Add(SyncAction.Overwrite, newer, item, null);
        }

        private SyncDirection Newer(FolderCompareItem item)
        {
            var difference = item.Left.LastWriteUtc - item.Right.LastWriteUtc;
            if (difference.Duration() <= _profile.TimeTolerance)
            {
                return SyncDirection.None;
            }

            return difference > TimeSpan.Zero ? SyncDirection.LeftToRight : SyncDirection.RightToLeft;
        }

        private void Add(SyncAction action, SyncDirection direction, FolderCompareItem item, string? reason)
        {
            var leftPath = Resolve(_comparison.LeftRoot, item.RelativePath);
            var rightPath = Resolve(_comparison.RightRoot, item.RelativePath);
            string? source = null;
            string? target = null;
            FileMeta? expected = null;
            if (direction != SyncDirection.None)
            {
                var toRight = direction == SyncDirection.LeftToRight;
                source = action == SyncAction.Delete ? null : toRight ? leftPath : rightPath;
                target = toRight ? rightPath : leftPath;
                var targetMeta = toRight ? item.Right : item.Left;
                expected = targetMeta.Exists ? targetMeta : null;
            }

            Operations.Add(new SyncOperation(action, direction, item.RelativePath, item.Kind, source, target, reason) { ExpectedTarget = expected });
        }

        private static string Resolve(string root, string relativePath) =>
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
