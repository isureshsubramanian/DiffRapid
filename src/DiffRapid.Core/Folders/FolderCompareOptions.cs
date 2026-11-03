using System;
using System.Collections.Generic;
using System.IO;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Folders;

/// <summary>The file comparison strategy.</summary>
public enum FolderCompareMethod { FullContents, QuickContents, BinaryContents, Date, DateAndSize, Size, Existence }

/// <summary>The type of an entry; symbolic links and junctions are links.</summary>
public enum FolderItemKind { File, Folder, Link }

/// <summary>The outcome for an entry or an aggregated directory.</summary>
public enum FolderCompareStatus { Identical, Different, LeftOnly, RightOnly, TypeConflict, Skipped, Error }

/// <summary>Immutable folder comparison settings. Glob paths use forward slashes; masks without slashes match entry names.</summary>
public sealed record FolderCompareOptions
{
    public bool Recursive { get; init; } = true;
    public FolderCompareMethod Method { get; init; } = FolderCompareMethod.FullContents;
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
    public bool StopAtFirstDifference { get; init; } = true;
    public IReadOnlyList<string> IncludeFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludeFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeFolders { get; init; } = Array.Empty<string>();
    public bool DefaultExcludes { get; init; } = true;

    /// <summary>Optional .flt rules applied in addition to masks and default exclusions.</summary>
    public FolderFilter? Filter { get; init; }
    public bool FollowSymlinks { get; init; }
    public bool CaseSensitive { get; init; }
    public CompareOptions TextOptions { get; init; } = CompareOptions.Default;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public long MaxTextFileSize { get; init; } = 64L * 1024 * 1024;
}

/// <summary>A snapshot of entry metadata. Size is zero for directories and absent entries.</summary>
public sealed record FileMeta(long Size, DateTime LastWriteUtc, FileAttributes Attributes, bool Exists);

/// <summary>A completed item. Directory children are complete when the directory is reported.</summary>
public sealed record FolderCompareItem(
    string RelativePath,
    FolderItemKind Kind,
    FolderCompareStatus Status,
    FileMeta Left,
    FileMeta Right,
    long? DifferenceCount,
    string? Error,
    IReadOnlyList<FolderCompareItem> Children);

/// <summary>The completed tree and ordered flat list. Counts exclude the synthetic root.</summary>
public sealed record FolderCompareResult(
    FolderCompareItem Root,
    IReadOnlyList<FolderCompareItem> Items,
    IReadOnlyDictionary<FolderCompareStatus, int> Counts);
