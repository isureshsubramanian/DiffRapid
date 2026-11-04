using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

    private static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Plans every file of <paramref name="patch"/> relative to <paramref name="root"/> without writing anything.
    /// </summary>
    public static FolderPatchPlan Plan(Patch patch, string root, PatchApplyOptions? options = null)
    {
        options ??= new PatchApplyOptions();
        var fullRoot = Path.GetFullPath(root);
        var files = patch.Files.Select(file => PlanUnderRoot(file, fullRoot, options)).ToList();
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
    public static FilePatchPlan PlanFile(FilePatch patch, string filePath, PatchApplyOptions? options = null)
    {
        options ??= new PatchApplyOptions();
        var fullPath = Path.GetFullPath(filePath);
        var deletes = (options.Reverse ? patch.OldPath : patch.NewPath) is null;
        var target = deletes ? null : fullPath;
        var action = deletes ? FilePatchAction.Delete : FilePatchAction.Modify;
        return PlanContent(patch, Path.GetFileName(fullPath), fullPath, target, action, options);
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

    /// <summary>Writes one planned file; see <see cref="Apply(FolderPatchPlan, Func{FilePatchPlan, bool}?)"/>.</summary>
    public static FilePatchWriteResult ApplyFile(FilePatchPlan file)
    {
        if (!file.CanApply)
        {
            return new FilePatchWriteResult(file, false, file.Message ?? "The file is not ready to apply.");
        }

        try
        {
            var stale = StaleReason(file);
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

    private static FilePatchPlan PlanUnderRoot(FilePatch patch, string root, PatchApplyOptions options)
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
            : string.Equals(source, target, PathComparison) ? FilePatchAction.Modify
            : FilePatchAction.Rename;
        return PlanContent(patch, display, source, target, action, options);
    }

    /// <summary>Reads the source, applies the hunks in memory and decides whether the result can be written.</summary>
    private static FilePatchPlan PlanContent(FilePatch patch, string display, string? source, string? target, FilePatchAction action, PatchApplyOptions options)
    {
        // A rename whose old file is gone and whose new file already holds the result has been applied.
        if (action == FilePatchAction.Rename && !File.Exists(source) && File.Exists(target))
        {
            var atTarget = PlanContent(patch, display, target, target, FilePatchAction.Modify, options);
            var applied = atTarget.Status == FilePatchStatus.AlreadyApplied || (patch.Hunks.Count == 0 && atTarget.Status == FilePatchStatus.Ready);
            if (applied)
            {
                return atTarget with { SourcePath = source, Action = FilePatchAction.Rename, Status = FilePatchStatus.AlreadyApplied };
            }
        }

        var readPath = source ?? target!;
        var exists = File.Exists(readPath);
        var decoded = exists ? TextFileReader.ReadFile(readPath) : new DecodedText(string.Empty, new UTF8Encoding(false), false, false);
        if (decoded.IsBinary || patch.IsBinary)
        {
            return Rejected(patch, display, FilePatchStatus.Binary, "Binary changes cannot be applied from a text patch.") with { SourcePath = source, TargetPath = target, Action = action };
        }

        var result = PatchApplier.Apply(decoded.Text, patch, options);
        var allAlreadyApplied = result.Outcomes.Count > 0 && result.Outcomes.All(o => o.Status == HunkStatus.AlreadyApplied);
        var problem = FileStateProblem(action, exists, decoded.Text, result, allAlreadyApplied);
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

    /// <summary>Rejects absolute paths, parent traversal, drive-qualified components, and links on the way.</summary>
    private static string Resolve(string root, string path)
    {
        var components = path.Split('/', '\\');
        if (Path.IsPathRooted(path) || components.Any(c => c == ".." || c.IndexOf(':') >= 0))
        {
            throw new UnsafePathException("Unsafe patch path: " + path);
        }

        var resolved = Path.GetFullPath(Path.Combine(root, path));
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = trimmedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, PathComparison))
        {
            throw new UnsafePathException("The patch path leaves the folder: " + path);
        }

        // Existing links can redirect an otherwise safe path outside the root.
        var current = resolved;
        while (current is not null && current.Length > trimmedRoot.Length)
        {
            var entryExists = File.Exists(current) || Directory.Exists(current);
            if (entryExists && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafePathException("The patch path goes through a link: " + path);
            }

            current = Path.GetDirectoryName(current);
        }

        return resolved;
    }

    /// <summary>Returns why the disk no longer matches the preview, or null when it still does.</summary>
    private static string? StaleReason(FilePatchPlan file)
    {
        if (file.SourcePath is null)
        {
            return file.TargetPath is not null && File.Exists(file.TargetPath) ? "The file was created since the preview." : null;
        }

        if (!File.Exists(file.SourcePath))
        {
            return "The file was removed since the preview.";
        }

        var current = TextFileReader.ReadFile(file.SourcePath).Text;
        return current == file.OriginalText ? null : "The file changed since the preview.";
    }

    private static void WriteChange(FilePatchPlan file)
    {
        if (file.TargetPath is null)
        {
            File.Delete(file.SourcePath!);
            return;
        }

        WriteAtomically(file.TargetPath, file.PatchedText, file.Encoding, file.HasByteOrderMark);
        if (file.Action == FilePatchAction.Rename)
        {
            File.Delete(file.SourcePath!);
        }
    }

    /// <summary>Writes through a temporary file in the same folder so a failure never leaves a half-written file.</summary>
    private static void WriteAtomically(string path, string text, Encoding encoding, bool byteOrderMark)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        try
        {
            var preamble = byteOrderMark ? encoding.GetPreamble() : Array.Empty<byte>();
            var content = encoding.GetBytes(text);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(content, 0, content.Length);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
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
