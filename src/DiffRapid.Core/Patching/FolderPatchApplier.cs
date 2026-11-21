using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Core.Folders;
using DiffRapid.Core.IO;
using DiffRapid.Core.Text;

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

    /// <summary>The folder the patch paths were resolved under, or null for a file chosen by the user.</summary>
    internal string? Root { get; init; }
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
    private static readonly IReadOnlyList<HunkOutcome> NoOutcomes = Array.Empty<HunkOutcome>();

    /// <summary>
    /// Plans every file of <paramref name="patch"/> relative to <paramref name="root"/> without writing anything.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static FolderPatchPlan Plan(Patch patch, string root, PatchApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PatchApplyOptions();
        var fullRoot = Path.GetFullPath(root);
        var files = patch.Files.Select(file => PlanUnderRoot(file, fullRoot, options, cancellationToken)).ToList();
        return new FolderPatchPlan(fullRoot, options, files.AsReadOnly());
    }

    /// <summary>
    /// When some files the patch changes or deletes are missing under <paramref name="root"/>, looks one folder
    /// up and one folder down for a root where more of them exist (a patch made from the repository root,
    /// applied to a project folder, or the other way round). Returns that folder, or null when the root already
    /// holds every file or no nearby folder does better.
    /// </summary>
    public static string? SuggestRoot(Patch patch, string root, PatchApplyOptions? options = null)
    {
        options ??= new PatchApplyOptions();
        var sources = patch.Files
            .Select(file => options.Reverse ? file.NewPath : file.OldPath)
            .Where(path => path is not null && !Path.IsPathRooted(path))
            .Select(path => path!.Replace('/', Path.DirectorySeparatorChar))
            .ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root);
        int Found(string folder) => sources.Count(source => File.Exists(Path.Combine(folder, source)));
        var current = Found(fullRoot);
        if (current == sources.Count)
        {
            return null;
        }

        var candidates = new List<string>();
        if (Path.GetDirectoryName(fullRoot) is { } parent)
        {
            candidates.Add(parent);
        }

        try
        {
            // A cap keeps a root like C:\ quick; the folder a patch belongs in is rarely past the first hundreds.
            candidates.AddRange(Directory.EnumerateDirectories(fullRoot).Take(500));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        string? best = null;
        var bestFound = current;
        foreach (var candidate in candidates)
        {
            var found = Found(candidate);
            if (found > bestFound)
            {
                (best, bestFound) = (candidate, found);
            }
        }

        return best;
    }

    /// <summary>
    /// Plans one file patch against <paramref name="filePath"/>, ignoring the paths in the patch
    /// (applying a patch to a file chosen by the user).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static FilePatchPlan PlanFile(FilePatch patch, string filePath, PatchApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PatchApplyOptions();
        var fullPath = Path.GetFullPath(filePath);
        var deletes = (options.Reverse ? patch.OldPath : patch.NewPath) is null;
        var target = deletes ? null : fullPath;
        var action = deletes ? FilePatchAction.Delete : FilePatchAction.Modify;
        return PlanContent(patch, Path.GetFileName(fullPath), fullPath, target, action, options, cancellationToken);
    }

    /// <summary>
    /// Writes the ready files of <paramref name="plan"/> accepted by <paramref name="include"/> (all when null).
    /// A file whose source changed on disk since planning is not written.
    /// </summary>
    public static IReadOnlyList<FilePatchWriteResult> Apply(FolderPatchPlan plan, Func<FilePatchPlan, bool>? include = null)
    {
        var results = new List<FilePatchWriteResult>();
        foreach (var file in plan.Files)
        {
            if (include is not null && !include(file))
            {
                continue;
            }

            results.Add(ApplyFile(file));
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Writes one planned file; see <see cref="Apply(FolderPatchPlan, Func{FilePatchPlan, bool}?)"/>. The containment
    /// and link checks of planning run again first, since a link may have been created after the preview.
    /// </summary>
    public static FilePatchWriteResult ApplyFile(FilePatchPlan file)
    {
        if (!file.CanApply)
        {
            return new FilePatchWriteResult(file, false, file.Message ?? "The file is not ready to apply.");
        }

        try
        {
            var stale = file.Root is null ? null
                : UnsafeReason(file.Root, file.SourcePath, file.DisplayPath) ?? UnsafeReason(file.Root, file.TargetPath, file.DisplayPath);
            stale ??= StaleReason(file);
            if (stale is not null)
            {
                return new FilePatchWriteResult(file, false, stale);
            }

            WriteChange(file);
            return new FilePatchWriteResult(file, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FilePatchWriteResult(file, false, ex.Message);
        }
    }

    private static FilePatchPlan PlanUnderRoot(FilePatch patch, string root, PatchApplyOptions options, CancellationToken cancellationToken)
    {
        var sourceName = options.Reverse ? patch.NewPath : patch.OldPath;
        var targetName = options.Reverse ? patch.OldPath : patch.NewPath;
        var display = targetName ?? sourceName ?? string.Empty;
        if (sourceName is not null && targetName is not null && sourceName != targetName)
        {
            display = sourceName + " → " + targetName;
        }

        if (sourceName is null && targetName is null)
        {
            return Rejected(patch, display, FilePatchStatus.Failed, "The patch has no file path.");
        }

        string? source;
        string? target;
        try
        {
            source = sourceName is null ? null : Resolve(root, sourceName);
            target = targetName is null ? null : Resolve(root, targetName);
        }
        catch (UnsafePathException ex)
        {
            return Rejected(patch, display, FilePatchStatus.Unsafe, ex.Message);
        }

        var action = source is null ? FilePatchAction.Add
            : target is null ? FilePatchAction.Delete
            // A target differing only in case is a rename on every platform (git records it as one), not a modify.
            : string.Equals(source, target, StringComparison.Ordinal) ? FilePatchAction.Modify
            : FilePatchAction.Rename;
        return PlanContent(patch, display, source, target, action, options, cancellationToken) with { Root = root };
    }

    /// <summary>Reads the source, applies the hunks in memory and decides whether the result can be written.</summary>
    private static FilePatchPlan PlanContent(FilePatch patch, string display, string? source, string? target, FilePatchAction action, PatchApplyOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A rename whose old file is gone and whose new file already holds the result has been applied.
        if (action == FilePatchAction.Rename && !File.Exists(source) && File.Exists(target))
        {
            var atTarget = PlanContent(patch, display, target, target, FilePatchAction.Modify, options, cancellationToken);
            var applied = atTarget.Status == FilePatchStatus.AlreadyApplied || (patch.Hunks.Count == 0 && atTarget.Status == FilePatchStatus.Ready);
            if (applied)
            {
                return atTarget with { SourcePath = source, Action = FilePatchAction.Rename, Status = FilePatchStatus.AlreadyApplied };
            }
        }

        var readPath = source ?? target!;
        var exists = File.Exists(readPath);
        var decoded = exists ? FolderComparer.ReadTextUnlessEmpty(readPath) : new DecodedText(string.Empty, new UTF8Encoding(false), false, false);
        if (decoded.IsBinary || patch.IsBinary)
        {
            return Rejected(patch, display, FilePatchStatus.Binary, "Binary changes cannot be applied from a text patch.") with { SourcePath = source, TargetPath = target, Action = action };
        }

        var result = PatchApplier.Apply(decoded.Text, patch, options, cancellationToken);
        var allAlreadyApplied = result.Outcomes.Count > 0 && result.Outcomes.All(o => o.Status == HunkStatus.AlreadyApplied);
        var problem = FileStateProblem(action, exists, decoded.Text, result, allAlreadyApplied);
        problem ??= action == FilePatchAction.Rename && TargetTaken(source!, target!) ? "The file to rename to already exists." : null;
        problem ??= FirstFailure(patch, result, options);

        var status = problem is not null ? FilePatchStatus.Failed
            : allAlreadyApplied || (action == FilePatchAction.Delete && !exists) ? FilePatchStatus.AlreadyApplied
            : FilePatchStatus.Ready;
        return new FilePatchPlan(
            patch,
            display,
            source,
            target,
            action,
            status,
            source is null ? string.Empty : decoded.Text,
            action == FilePatchAction.Delete ? string.Empty : result.Text,
            result.Outcomes,
            problem)
        {
            Encoding = decoded.Encoding,
            HasByteOrderMark = decoded.HasByteOrderMark,
        };
    }

    /// <summary>Checks that the file's presence fits the action (the rules of patch(1) and git apply).</summary>
    private static string? FileStateProblem(FilePatchAction action, bool exists, string original, PatchApplyResult result, bool allAlreadyApplied)
    {
        switch (action)
        {
            case FilePatchAction.Add:
            {
                // An existing file is only acceptable when it already holds the added content.
                var acceptable = !exists || ((result.Outcomes.Count > 0 || original.Length == 0) && allAlreadyApplied);
                return acceptable ? null : "The file to add already exists with other content.";
            }

            case FilePatchAction.Delete:
            {
                // A deletion must consume the whole file, or the file must already be gone.
                var acceptable = !exists || (result.Succeeded && result.Text.Length == 0);
                return acceptable ? null : "The file to delete has content the patch does not remove.";
            }

            default:
                return exists ? null : "The file does not exist.";
        }
    }

    /// <summary>
    /// Whether a rename would overwrite another file (git apply refuses with "already exists"). A target that differs
    /// from the source only in case is the source itself on a case-insensitive file system, so then it counts only
    /// when entries spelled exactly like both the source and the target exist (a case-sensitive file system).
    /// </summary>
    private static bool TargetTaken(string source, string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return false;
        }

        return !IsCaseOnly(source, target) || !SameEntry(source, target);
    }

    private static bool IsCaseOnly(string source, string target) =>
        string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && !string.Equals(source, target, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="target"/>, which differs from the existing <paramref name="source"/> only in case,
    /// names the same entry: no folder lists a name spelled exactly like the target's where the spellings differ
    /// (in the file name or in any folder above it). Only folders on the source's own path are listed.
    /// </summary>
    private static bool SameEntry(string source, string target)
    {
        for (string? from = source, to = target; from is not null && to is not null; from = Path.GetDirectoryName(from), to = Path.GetDirectoryName(to))
        {
            var name = Path.GetFileName(to);
            if (name.Length == 0 || string.Equals(Path.GetFileName(from), name, StringComparison.Ordinal))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(from);
            if (parent is null || Directory.EnumerateFileSystemEntries(parent).Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static string? FirstFailure(FilePatch patch, PatchApplyResult result, PatchApplyOptions options)
    {
        for (var i = 0; i < result.Outcomes.Count; i++)
        {
            if (result.Outcomes[i].Status == HunkStatus.Failed)
            {
                var hunk = patch.Hunks[i];
                var line = options.Reverse ? hunk.NewStart : hunk.OldStart;
                return $"Hunk {i + 1} did not match near line {line}.";
            }
        }

        return null;
    }

    private static FilePatchPlan Rejected(FilePatch patch, string display, FilePatchStatus status, string message) =>
        new(patch, display, null, null, FilePatchAction.Modify, status, string.Empty, string.Empty, NoOutcomes, message);

    /// <summary>Rejects absolute paths, parent traversal, drive-qualified components, NUL characters, and links on the way.</summary>
    private static string Resolve(string root, string path)
    {
        var components = path.Split('/', '\\');
        if (path.IndexOf('\0') >= 0 || Path.IsPathRooted(path) || components.Any(c => c == ".." || c.IndexOf(':') >= 0))
        {
            throw new UnsafePathException("Unsafe patch path: " + path);
        }

        var resolved = Path.GetFullPath(Path.Combine(root, path));
        var problem = UnsafeReason(root, resolved, path);
        return problem is null ? resolved : throw new UnsafePathException(problem);
    }

    /// <summary>Why <paramref name="fullPath"/> may not be touched under <paramref name="root"/>, or null when it may.</summary>
    private static string? UnsafeReason(string root, string? fullPath, string shown)
    {
        if (fullPath is null)
        {
            return null;
        }

        if (PathContainment.RelativeInside(root, fullPath) is null)
        {
            return "The patch path leaves the folder: " + shown;
        }

        // Existing links can redirect an otherwise safe path outside the root. A folder that cannot be read cannot be
        // checked, so only this file is refused rather than the whole patch.
        try
        {
            return PathContainment.PassesThroughLink(root, fullPath) ? "The patch path goes through a link: " + shown : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The patch path cannot be checked for links: " + shown;
        }
    }

    /// <summary>Returns why the disk no longer matches the preview, or null when it still does.</summary>
    private static string? StaleReason(FilePatchPlan file)
    {
        if (file.Action == FilePatchAction.Rename && TargetTaken(file.SourcePath!, file.TargetPath!))
        {
            return "The file to rename to was created since the preview.";
        }

        if (file.SourcePath is null)
        {
            return file.TargetPath is not null && File.Exists(file.TargetPath) ? "The file was created since the preview." : null;
        }

        if (!File.Exists(file.SourcePath))
        {
            return "The file was removed since the preview.";
        }

        var current = FolderComparer.ReadTextUnlessEmpty(file.SourcePath).Text;
        return current == file.OriginalText ? null : "The file changed since the preview.";
    }

    private static void WriteChange(FilePatchPlan file)
    {
        if (file.TargetPath is null)
        {
            File.Delete(file.SourcePath!);
            return;
        }

        var preamble = file.HasByteOrderMark ? file.Encoding.GetPreamble() : Array.Empty<byte>();
        var content = file.Encoding.GetBytes(file.PatchedText);
        // On a case-insensitive file system a case-only target is the source itself (it exists, and the plan found
        // it to be the same entry): writing it and then deleting the source would delete the result. So the content
        // goes over the source, which is then renamed. When the case differs in a folder name, the folder is shared
        // with other files and is left as it is, so the change applies as an edit. On a case-sensitive file system
        // the target does not exist, and the ordinary rename below creates its folder.
        var caseOnly = file.Action == FilePatchAction.Rename && IsCaseOnly(file.SourcePath!, file.TargetPath) && File.Exists(file.TargetPath);
        AtomicFile.Write(caseOnly ? file.SourcePath! : file.TargetPath, stream =>
        {
            stream.Write(preamble, 0, preamble.Length);
            stream.Write(content, 0, content.Length);
        });
        if (caseOnly && !string.Equals(Path.GetFileName(file.SourcePath), Path.GetFileName(file.TargetPath), StringComparison.Ordinal))
        {
            // Only the file name is renamed; the folders keep their spelling (see above).
            RenameCase(file.SourcePath!, Path.Combine(Path.GetDirectoryName(file.SourcePath)!, Path.GetFileName(file.TargetPath)));
        }
        else if (file.Action == FilePatchAction.Rename && !caseOnly)
        {
            File.Delete(file.SourcePath!);
        }
    }

    /// <summary>
    /// Renames a file to a name that differs only in case. The move goes through a temporary name, since a direct
    /// move between two spellings of one file may change nothing; when the second step fails the first is undone.
    /// </summary>
    private static void RenameCase(string source, string target)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(source)!, ".~dr" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        File.Move(source, temporary);
        try
        {
            File.Move(temporary, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(temporary, source);
            throw;
        }
    }

    private sealed class UnsafePathException : Exception
    {
        public UnsafePathException(string message)
            : base(message)
        {
        }
    }
}
