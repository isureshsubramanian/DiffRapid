using System;
using System.Collections.Generic;
using System.IO;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Folders;

/// <summary>The file comparison strategy. <see cref="Hash"/> compares sizes, then 64-bit xxHash values of the contents.</summary>
public enum FolderCompareMethod { FullContents, QuickContents, BinaryContents, Date, DateAndSize, Size, Existence, Hash }

/// <summary>The type of an entry; symbolic links and junctions are links.</summary>
public enum FolderItemKind { File, Folder, Link }

/// <summary>
/// The outcome for an entry or an aggregated directory. <see cref="Renamed"/> (same folder, new name) and
/// <see cref="Moved"/> (another folder) appear only when rename detection is on; both files of a pair keep
/// their own entry and name the other in <see cref="FolderCompareItem.CounterpartPath"/>.
/// </summary>
public enum FolderCompareStatus { Identical, Different, LeftOnly, RightOnly, TypeConflict, Skipped, Error, Renamed, Moved }

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

    /// <summary>Pairs files that exist only on the left with files that exist only on the right when their contents match.</summary>
    public bool DetectRenames { get; init; }

    /// <summary>
    /// With <see cref="DetectRenames"/>, also pairs text files whose sets of non-blank lines overlap by at least this
    /// Jaccard similarity (above 0, at most 1). Null pairs identical contents only.
    /// </summary>
    public double? RenameSimilarityThreshold { get; init; }
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
    IReadOnlyList<FolderCompareItem> Children)
{
    /// <summary>For a renamed or moved file, the relative path of the paired file on the other side.</summary>
    public string? CounterpartPath { get; init; }

    /// <summary>For a renamed or moved file, how alike the pair is: 1 for identical contents, else the line-set similarity.</summary>
    public double? Similarity { get; init; }
}

/// <summary>
/// A streamed item. <see cref="CompletedCount"/> counts finished items, the root included. A revision re-reports
/// an item whose status changed after the scan (a rename pairing) and does not advance the count.
/// </summary>
public sealed record FolderCompareProgress(FolderCompareItem Item, int CompletedCount)
{
    /// <summary>True when the item was reported before and this report replaces it.</summary>
    public bool IsRevision { get; init; }
}

/// <summary>The completed tree and ordered flat list. Counts exclude the synthetic root.</summary>
public sealed record FolderCompareResult(
    FolderCompareItem Root,
    IReadOnlyList<FolderCompareItem> Items,
    IReadOnlyDictionary<FolderCompareStatus, int> Counts)
{
    /// <summary>Absolute root of the left comparison tree.</summary>
    public string LeftRoot { get; init; } = string.Empty;
    /// <summary>Absolute root of the right comparison tree.</summary>
    public string RightRoot { get; init; } = string.Empty;
}
