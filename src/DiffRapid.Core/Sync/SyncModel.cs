using System;
using System.Collections.Generic;
using System.Linq;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Sync;

/// <summary>How a sync makes the two folders agree.</summary>
public enum SyncMode
{
    /// <summary>Make the right folder an exact copy of the left: copy, overwrite and delete on the right.</summary>
    MirrorLeftToRight,

    /// <summary>Make the left folder an exact copy of the right: copy, overwrite and delete on the left.</summary>
    MirrorRightToLeft,

    /// <summary>Copy missing and newer files from left to right; never delete.</summary>
    Update,

    /// <summary>Copy missing and newer files in both directions; never delete.</summary>
    UpdateBoth,
}

/// <summary>Which way an operation copies or which side it changes.</summary>
public enum SyncDirection
{
    /// <summary>Reads the left side and changes the right side.</summary>
    LeftToRight,

    /// <summary>Reads the right side and changes the left side.</summary>
    RightToLeft,

    /// <summary>Changes neither side (skips and conflicts).</summary>
    None,
}

/// <summary>The kind of a planned sync operation.</summary>
public enum SyncAction
{
    /// <summary>Copy a file to a side where it is missing.</summary>
    Copy,

    /// <summary>Replace a different file on the target side.</summary>
    Overwrite,

    /// <summary>Delete a file or (empty) folder on the target side.</summary>
    Delete,

    /// <summary>Create a folder missing on the target side.</summary>
    CreateFolder,

    /// <summary>Leave the entry alone; <see cref="SyncOperation.Reason"/> says why.</summary>
    Skip,

    /// <summary>Both sides changed and the plan cannot pick a winner.</summary>
    Conflict,
}

/// <summary>
/// Sync settings. <see cref="Filter"/> is applied on top of the filters already used by the folder comparison;
/// excluded entries are never copied or deleted.
/// </summary>
public sealed record SyncProfile
{
    /// <summary>The sync direction and whether deletions happen.</summary>
    public SyncMode Mode { get; init; } = SyncMode.MirrorLeftToRight;

    /// <summary>Optional .flt rules limiting what is synchronized.</summary>
    public FolderFilter? Filter { get; init; }

    /// <summary>Times closer than this count as equal when deciding which file is newer.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When the folders were last synchronized. In <see cref="SyncMode.UpdateBoth"/>, a file changed on both
    /// sides after this time is a conflict instead of "newer wins".
    /// </summary>
    public DateTime? LastSyncUtc { get; init; }
}

/// <summary>
/// One planned operation. Paths are absolute; <see cref="SourcePath"/> is null for deletions, skips and conflicts.
/// <see cref="ExpectedTarget"/> is what the target looked like when planned, so execution can refuse to
/// overwrite or delete something that changed in the meantime.
/// </summary>
public sealed record SyncOperation(
    SyncAction Action,
    SyncDirection Direction,
    string RelativePath,
    FolderItemKind Kind,
    string? SourcePath,
    string? TargetPath,
    string? Reason)
{
    /// <summary>Metadata of the target when planned, or null when it did not exist or does not matter.</summary>
    public FileMeta? ExpectedTarget { get; init; }

    /// <summary>Whether this operation changes the file system when executed.</summary>
    public bool ChangesFiles => Action is not (SyncAction.Skip or SyncAction.Conflict);
}

/// <summary>A preview of the operations a sync would perform, in execution order.</summary>
public sealed record SyncPlan(string LeftRoot, string RightRoot, SyncProfile Profile, IReadOnlyList<SyncOperation> Operations)
{
    /// <summary>Whether any operation is a conflict.</summary>
    public bool HasConflicts => Operations.Any(o => o.Action == SyncAction.Conflict);

    /// <summary>Whether the plan changes nothing.</summary>
    public bool IsEmpty => !Operations.Any(o => o.ChangesFiles);

    /// <summary>The number of operations of one kind.</summary>
    public int Count(SyncAction action) => Operations.Count(o => o.Action == action);
}
