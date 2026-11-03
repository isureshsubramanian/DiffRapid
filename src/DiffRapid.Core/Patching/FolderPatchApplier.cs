using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiffRapid.Core.Patching;

/// <summary>What applying a file patch does to the file system.</summary>
public enum FilePatchAction
{
    /// <summary>Changes an existing file in place.</summary>
    Modify,

    /// <summary>Creates a new file.</summary>
    Add,

    /// <summary>Removes an existing file.</summary>
    Delete,

    /// <summary>Changes a file and writes it under a new path.</summary>
    Rename,
}

/// <summary>Whether a planned file patch can be written.</summary>
public enum FilePatchStatus
{
    /// <summary>Every hunk matched; the file can be written.</summary>
    Ready,

    /// <summary>Every hunk was already present; writing changes nothing.</summary>
    AlreadyApplied,

    /// <summary>At least one hunk did not match, or the file is missing or unexpectedly present.</summary>
    Failed,

    /// <summary>The file or the patch is binary and cannot be applied as text.</summary>
    Binary,

    /// <summary>The path is absolute, climbs out of the root, or passes through a link.</summary>
    Unsafe,
}

/// <summary>The preview of one file patch: what it would write, without touching the disk.</summary>
/// <param name="Patch">The file patch from the parsed patch file.</param>
/// <param name="DisplayPath">The patch's relative path, shown to the user ('/' separators).</param>
/// <param name="SourcePath">Full path of the file read, or null for an addition (or an unsafe path).</param>
/// <param name="TargetPath">Full path of the file written, or null for a deletion (or an unsafe path).</param>
/// <param name="Action">What applying the patch does.</param>
/// <param name="Status">Whether the patch can be applied.</param>
/// <param name="OriginalText">The source file's text; empty for additions or unreadable files.</param>
/// <param name="PatchedText">The text after applying every matching hunk; empty for deletions.</param>
/// <param name="Outcomes">The result for each hunk in patch order.</param>
/// <param name="Message">Why the file cannot be applied, or null.</param>
public sealed record FilePatchPlan(
    FilePatch Patch,
    string DisplayPath,
    string? SourcePath,
    string? TargetPath,
    FilePatchAction Action,
    FilePatchStatus Status,
    string OriginalText,
    string PatchedText,
    IReadOnlyList<HunkOutcome> Outcomes,
    string? Message)
{
    /// <summary>Encoding used to write the target; the source file's encoding, UTF-8 for additions.</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);

    /// <summary>Whether the written file starts with the encoding's byte order mark.</summary>
    public bool HasByteOrderMark { get; init; }

    /// <summary>True when the file can be written.</summary>
    public bool CanApply => Status == FilePatchStatus.Ready;
}

/// <summary>The preview of a whole patch applied under a root folder.</summary>
/// <param name="Root">Full path of the folder that patch paths are relative to.</param>
/// <param name="Options">The options used to match hunks.</param>
/// <param name="Files">One plan per file patch, in patch order.</param>
public sealed record FolderPatchPlan(string Root, PatchApplyOptions Options, IReadOnlyList<FilePatchPlan> Files)
{
    /// <summary>True when every file is ready or already applied.</summary>
    public bool AllApplicable => Files.All(f => f.Status is FilePatchStatus.Ready or FilePatchStatus.AlreadyApplied);
}

/// <summary>What happened when one planned file was written.</summary>
/// <param name="File">The plan that was written.</param>
/// <param name="Written">True when the file system was changed.</param>
/// <param name="Error">Why the file was not written, or null.</param>
public sealed record FilePatchWriteResult(FilePatchPlan File, bool Written, string? Error);

/// <summary>Previews and applies a patch to files under a folder, or to a single chosen file.</summary>
public static class FolderPatchApplier
{
    /// <summary>
    /// Plans every file of <paramref name="patch"/> relative to <paramref name="root"/> without writing anything.
    /// </summary>
    public static FolderPatchPlan Plan(Patch patch, string root, PatchApplyOptions? options = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Plans one file patch against <paramref name="filePath"/>, ignoring the paths in the patch
    /// (applying a patch to a file chosen by the user).
    /// </summary>
    public static FilePatchPlan PlanFile(FilePatch patch, string filePath, PatchApplyOptions? options = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the ready files of <paramref name="plan"/> accepted by <paramref name="include"/> (all when null).
    /// A file whose source changed on disk since planning is not written.
    /// </summary>
    public static IReadOnlyList<FilePatchWriteResult> Apply(FolderPatchPlan plan, Func<FilePatchPlan, bool>? include = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>Writes one planned file; see <see cref="Apply(FolderPatchPlan, Func{FilePatchPlan, bool}?)"/>.</summary>
    public static FilePatchWriteResult ApplyFile(FilePatchPlan file)
    {
        throw new NotImplementedException();
    }
}
