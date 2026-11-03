using System;
using System.IO;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>Writes text changes in a directory comparison as git-style unified diffs.</summary>
public static class FolderPatchWriter
{
    /// <summary>Writes changes using the roots retained by the folder comparison.</summary>
    public static void Write(TextWriter writer, FolderCompareResult result, CompareOptions options, int contextLines = 3)
    {
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines));
        }

        foreach (var item in result.Items)
        {
            if (item.Status == FolderCompareStatus.Error || item.Status == FolderCompareStatus.TypeConflict)
            {
                throw new IOException("Cannot generate patch for " + item.RelativePath + ": " + item.Status);
            }

            if (item.Kind != FolderItemKind.File || item.Status is not (FolderCompareStatus.Different or FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly))
            {
                continue;
            }

            var path = item.RelativePath.Replace('\\', '/');
            var left = item.Left.Exists ? TextFileReader.ReadFile(Path.Combine(result.LeftRoot, path)) : null;
            var right = item.Right.Exists ? TextFileReader.ReadFile(Path.Combine(result.RightRoot, path)) : null;
            writer.Write("diff --git a/" + path + " b/" + path + "\n");
            if (left?.IsBinary == true || right?.IsBinary == true)
            {
                writer.Write("Binary files a/" + path + " and b/" + path + " differ\n");
                continue;
            }

            var diff = TextComparer.Compare(TextDocument.Parse(left?.Text ?? ""), TextDocument.Parse(right?.Text ?? ""), options);
            if (diff.AreEqual && (left is null || right is null))
            {
                writer.Write("--- " + (left is null ? "/dev/null" : "a/" + path) + "\n+++ " + (right is null ? "/dev/null" : "b/" + path) + "\n");
            }
            else
            {
                UnifiedDiffWriter.Write(writer, diff, left is null ? "/dev/null" : "a/" + path, right is null ? "/dev/null" : "b/" + path, contextLines);
            }
        }
    }
}
