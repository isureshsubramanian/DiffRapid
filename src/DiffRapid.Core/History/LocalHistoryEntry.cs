using System;

namespace DiffRapid.Core.History;

/// <summary>Metadata for one captured version of a file.</summary>
/// <param name="TimestampUtc">The capture time in UTC.</param>
/// <param name="Hash">The lowercase SHA-256 hash of the uncompressed content.</param>
/// <param name="Size">The uncompressed content size in bytes.</param>
/// <param name="Label">The reason for capturing this version, such as Saved or Before merge.</param>
/// <param name="Note">An optional description supplied by the caller.</param>
/// <param name="OriginalPath">The normalized full path at capture time.</param>
public sealed record LocalHistoryEntry(
    DateTimeOffset TimestampUtc,
    string Hash,
    long Size,
    string Label,
    string? Note,
    string OriginalPath);
