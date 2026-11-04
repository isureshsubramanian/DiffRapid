using System;

namespace DiffRapid.Core.History;

/// <summary>Limits for capturing and retaining local file history.</summary>
public sealed record LocalHistoryOptions
{
    /// <summary>Maximum uncompressed snapshot size in bytes; defaults to 5 MB.</summary>
    public long MaxFileSize { get; init; } = 5 * 1024 * 1024;

    /// <summary>Whether to capture files containing NUL in their first 8000 bytes.</summary>
    public bool IncludeBinary { get; init; }

    /// <summary>Maximum snapshot age when pruning; defaults to 30 days.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Maximum retained snapshots per file when pruning; defaults to 100.</summary>
    public int MaxEntriesPerFile { get; init; } = 100;

    /// <summary>Maximum compressed size of distinct referenced blobs when pruning; defaults to 500 MB.</summary>
    public long MaxTotalSize { get; init; } = 500 * 1024 * 1024;
}
