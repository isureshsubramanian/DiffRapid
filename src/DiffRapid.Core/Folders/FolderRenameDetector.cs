using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Folders;

/// <summary>
/// Pairs files that exist only on the left with files that exist only on the right. Identical contents are found
/// by size, then xxHash64; similar text files (optional) by the Jaccard similarity of their sets of non-blank lines.
/// A group of identical files pairs by matching file names, or when exactly one file is left on each side;
/// anything else is ambiguous and stays unpaired. Empty files are never paired.
/// </summary>
internal static class FolderRenameDetector
{
    private const int TextProbeSize = 8000;
    private static readonly string[] LineBreaks = { "\r\n", "\n", "\r" };

    /// <summary>Returns the root with paired files marked Renamed or Moved, and adds every changed item to <paramref name="revised"/>.</summary>
    public static FolderCompareItem Resolve(
        FolderCompareItem root,
        string leftRoot,
        string rightRoot,
        FolderCompareOptions options,
        List<FolderCompareItem> revised,
        CancellationToken cancellationToken)
    {
        var lefts = new List<Candidate>();
        var rights = new List<Candidate>();
        Collect(root, leftRoot, rightRoot, lefts, rights);
        if (lefts.Count == 0 || rights.Count == 0)
        {
            return root;
        }

        var names = options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var pairs = new List<Pair>();
        PairIdentical(lefts, rights, names, pairs, cancellationToken);
        if (options.RenameSimilarityThreshold is { } threshold)
        {
            var pairedLefts = new HashSet<Candidate>(pairs.Select(p => p.Left));
            var pairedRights = new HashSet<Candidate>(pairs.Select(p => p.Right));
            PairSimilar(
                lefts.Where(c => !pairedLefts.Contains(c)).ToList(),
                rights.Where(c => !pairedRights.Contains(c)).ToList(),
                threshold,
                options.MaxTextFileSize,
                names,
                pairs,
                cancellationToken);
        }

        if (pairs.Count == 0)
        {
            return root;
        }

        var replacements = new Dictionary<string, FolderCompareItem>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var status = names.Equals(Folder(pair.Left.Item.RelativePath), Folder(pair.Right.Item.RelativePath))
                ? FolderCompareStatus.Renamed
                : FolderCompareStatus.Moved;
            var left = pair.Left.Item with { Status = status, CounterpartPath = pair.Right.Item.RelativePath, Similarity = pair.Similarity };
            var right = pair.Right.Item with { Status = status, CounterpartPath = pair.Left.Item.RelativePath, Similarity = pair.Similarity };
            replacements[left.RelativePath] = left;
            replacements[right.RelativePath] = right;
            revised.Add(left);
            revised.Add(right);
        }

        return Rebuild(root, replacements);
    }

    private static void Collect(FolderCompareItem item, string leftRoot, string rightRoot, List<Candidate> lefts, List<Candidate> rights)
    {
        foreach (var child in item.Children)
        {
            if (child.Kind == FolderItemKind.File && child.Status == FolderCompareStatus.LeftOnly && child.Left.Size > 0)
            {
                lefts.Add(new Candidate(child, FullPath(leftRoot, child.RelativePath), child.Left.Size));
            }
            else if (child.Kind == FolderItemKind.File && child.Status == FolderCompareStatus.RightOnly && child.Right.Size > 0)
            {
                rights.Add(new Candidate(child, FullPath(rightRoot, child.RelativePath), child.Right.Size));
            }

            Collect(child, leftRoot, rightRoot, lefts, rights);
        }
    }

    private static void PairIdentical(List<Candidate> lefts, List<Candidate> rights, StringComparer names, List<Pair> pairs, CancellationToken cancellationToken)
    {
        var rightsBySize = rights.ToLookup(c => c.Size);
        foreach (var sizeGroup in lefts.GroupBy(c => c.Size))
        {
            if (!rightsBySize.Contains(sizeGroup.Key))
            {
                continue;
            }

            var leftHashes = Hash(sizeGroup, cancellationToken);
            var rightHashes = Hash(rightsBySize[sizeGroup.Key], cancellationToken).ToLookup(h => h.Hash, h => h.Candidate);
            foreach (var hashGroup in leftHashes.GroupBy(h => h.Hash, h => h.Candidate))
            {
                if (rightHashes.Contains(hashGroup.Key))
                {
                    PairGroup(hashGroup.ToList(), rightHashes[hashGroup.Key].ToList(), names, pairs);
                }
            }
        }
    }

    private static List<(Candidate Candidate, ulong Hash)> Hash(IEnumerable<Candidate> candidates, CancellationToken cancellationToken)
    {
        var hashes = new List<(Candidate, ulong)>();
        foreach (var candidate in candidates)
        {
            try
            {
                hashes.Add((candidate, XxHash64.ComputeFile(candidate.FullPath, cancellationToken)));
            }
            catch (IOException)
            {
                // An unreadable file cannot be matched; it stays left or right only.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }

        return hashes;
    }

    private static void PairGroup(List<Candidate> lefts, List<Candidate> rights, StringComparer names, List<Pair> pairs)
    {
        var leftNames = lefts.ToLookup(c => FileName(c), names);
        var rightNames = rights.ToLookup(c => FileName(c), names);
        foreach (var name in leftNames)
        {
            if (name.Count() == 1 && rightNames[name.Key].Count() == 1)
            {
                var left = name.Single();
                var right = rightNames[name.Key].Single();
                pairs.Add(new Pair(left, right, 1));
                lefts.Remove(left);
                rights.Remove(right);
            }
        }

        if (lefts.Count == 1 && rights.Count == 1)
        {
            pairs.Add(new Pair(lefts[0], rights[0], 1));
        }
    }

    private static void PairSimilar(
        List<Candidate> lefts,
        List<Candidate> rights,
        double threshold,
        long maxTextFileSize,
        StringComparer names,
        List<Pair> pairs,
        CancellationToken cancellationToken)
    {
        if (lefts.Count == 0 || rights.Count == 0)
        {
            return;
        }

        var leftLines = lefts.Select(c => LineSet(c, maxTextFileSize, cancellationToken)).ToArray();
        var rightLines = rights.Select(c => LineSet(c, maxTextFileSize, cancellationToken)).ToArray();

        // An inverted index finds the files that share a line without comparing every pair.
        var index = new Dictionary<ulong, List<int>>();
        for (var j = 0; j < rightLines.Length; j++)
        {
            foreach (var line in rightLines[j])
            {
                if (!index.TryGetValue(line, out var files))
                {
                    files = new List<int>();
                    index.Add(line, files);
                }

                files.Add(j);
            }
        }

        var scored = new List<Pair>();
        for (var i = 0; i < leftLines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shared = new Dictionary<int, int>();
            foreach (var line in leftLines[i])
            {
                if (!index.TryGetValue(line, out var files))
                {
                    continue;
                }

                foreach (var j in files)
                {
                    shared.TryGetValue(j, out var count);
                    shared[j] = count + 1;
                }
            }

            foreach (var match in shared)
            {
                var similarity = match.Value / (double)(leftLines[i].Count + rightLines[match.Key].Count - match.Value);
                if (similarity >= threshold)
                {
                    scored.Add(new Pair(lefts[i], rights[match.Key], similarity));
                }
            }
        }

        AssignGreedily(scored, names, pairs);
    }

    // Best similarity first; on a tie a pair that keeps its file name wins, then path order keeps the result stable.
    private static void AssignGreedily(List<Pair> scored, StringComparer names, List<Pair> pairs)
    {
        var usedLefts = new HashSet<Candidate>();
        var usedRights = new HashSet<Candidate>();
        var ordered = scored
            .OrderByDescending(p => p.Similarity)
            .ThenByDescending(p => names.Equals(FileName(p.Left), FileName(p.Right)))
            .ThenBy(p => p.Left.Item.RelativePath, StringComparer.Ordinal)
            .ThenBy(p => p.Right.Item.RelativePath, StringComparer.Ordinal);
        foreach (var pair in ordered)
        {
            if (usedLefts.Contains(pair.Left) || usedRights.Contains(pair.Right))
            {
                continue;
            }

            usedLefts.Add(pair.Left);
            usedRights.Add(pair.Right);
            pairs.Add(pair);
        }
    }

    private static HashSet<ulong> LineSet(Candidate candidate, long maxTextFileSize, CancellationToken cancellationToken)
    {
        var lines = new HashSet<ulong>();
        if (candidate.Size > maxTextFileSize)
        {
            return lines;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(candidate.FullPath);
        }
        catch (IOException)
        {
            return lines;
        }
        catch (UnauthorizedAccessException)
        {
            return lines;
        }

        if (!LooksLikeText(bytes))
        {
            return lines;
        }

        foreach (var line in TextFileReader.Decode(bytes).Text.Split(LineBreaks, StringSplitOptions.None))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                lines.Add(XxHash64.Compute(Encoding.UTF8.GetBytes(trimmed)));
            }
        }

        return lines;
    }

    // A NUL byte near the start means binary, unless a UTF-16/32 byte order mark says otherwise.
    private static bool LooksLikeText(byte[] bytes)
    {
        var count = Math.Min(bytes.Length, TextProbeSize);
        var hasUnicodeBom = (count >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            || (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF);
        return hasUnicodeBom || Array.IndexOf(bytes, (byte)0, 0, count) < 0;
    }

    private static FolderCompareItem Rebuild(FolderCompareItem item, Dictionary<string, FolderCompareItem> replacements)
    {
        if (item.Kind == FolderItemKind.File && replacements.TryGetValue(item.RelativePath, out var replacement))
        {
            return replacement;
        }

        if (item.Children.Count == 0)
        {
            return item;
        }

        var children = new FolderCompareItem[item.Children.Count];
        var changed = false;
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = Rebuild(item.Children[i], replacements);
            changed |= !ReferenceEquals(children[i], item.Children[i]);
        }

        return changed ? item with { Children = Array.AsReadOnly(children) } : item;
    }

    private static string FullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FileName(Candidate candidate) =>
        candidate.Item.RelativePath.Substring(candidate.Item.RelativePath.LastIndexOf('/') + 1);

    private static string Folder(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath.Substring(0, slash);
    }

    private sealed class Candidate
    {
        public Candidate(FolderCompareItem item, string fullPath, long size)
        {
            Item = item;
            FullPath = fullPath;
            Size = size;
        }

        public FolderCompareItem Item { get; }

        public string FullPath { get; }

        public long Size { get; }
    }

    private sealed record Pair(Candidate Left, Candidate Right, double Similarity);
}
