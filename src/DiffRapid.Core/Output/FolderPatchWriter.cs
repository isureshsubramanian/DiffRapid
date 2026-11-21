using System;
using System.IO;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Patching;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>Writes text changes in a directory comparison as git-style unified diffs.</summary>
public static class FolderPatchWriter
{
    /// <summary>
    /// Writes changes using the roots retained by the folder comparison. A renamed or moved file is written once, as a
    /// git rename (rename from/to plus any text change). Paths with control characters, quotes or backslashes are
    /// quoted the way git quotes them.
    /// </summary>
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

            if (item.Kind != FolderItemKind.File)
            {
                continue;
            }

            var path = Slashes(item.RelativePath);
            if (item.Status is FolderCompareStatus.Renamed or FolderCompareStatus.Moved)
            {
                // Both files of the pair are listed; the left one writes the rename.
                if (item.Left.Exists && item.CounterpartPath is { } counterpart)
                {
                    WriteFile(writer, result, options, contextLines, path, Slashes(counterpart), rename: true, item.Similarity == 1);
                }

                continue;
            }

            if (item.Status is FolderCompareStatus.Different or FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly)
            {
                WriteFile(writer, result, options, contextLines, item.Left.Exists ? path : null, item.Right.Exists ? path : null, rename: false, identical: false);
            }
        }
    }

    private static void WriteFile(TextWriter writer, FolderCompareResult result, CompareOptions options, int contextLines,
        string? leftPath, string? rightPath, bool rename, bool identical)
    {
        var oldName = "a/" + (leftPath ?? rightPath);
        var newName = "b/" + (rightPath ?? leftPath);
        var left = leftPath is null ? null : TextFileReader.ReadFile(Path.Combine(result.LeftRoot, leftPath));
        var right = rightPath is null ? null : TextFileReader.ReadFile(Path.Combine(result.RightRoot, rightPath));
        writer.Write("diff --git " + PatchPath.Quote(oldName) + " " + PatchPath.Quote(newName) + "\n");
        if (rename)
        {
            writer.Write("rename from " + PatchPath.Quote(leftPath!) + "\nrename to " + PatchPath.Quote(rightPath!) + "\n");
        }

        if (left?.IsBinary == true || right?.IsBinary == true)
        {
            if (!identical)
            {
                writer.Write("Binary files " + PatchPath.Quote(oldName) + " and " + PatchPath.Quote(newName) + " differ\n");
            }

            return;
        }

        var oldLabel = left is null ? "/dev/null" : PatchPath.Quote(oldName);
        var newLabel = right is null ? "/dev/null" : PatchPath.Quote(newName);
        var diff = TextComparer.Compare(TextDocument.Parse(left?.Text ?? ""), TextDocument.Parse(right?.Text ?? ""), options);
        if (diff.AreEqual && (left is null || right is null))
        {
            writer.Write("--- " + oldLabel + "\n+++ " + newLabel + "\n");
        }
        else
        {
            // An unchanged rename has no hunks and no file header: the rename lines say everything.
            UnifiedDiffWriter.Write(writer, diff, oldLabel, newLabel, contextLines);
        }
    }

    /// <summary>Patch paths use '/'; on Unix a backslash is part of a name and is kept (and quoted).</summary>
    private static string Slashes(string relativePath) => relativePath.Replace(Path.DirectorySeparatorChar, '/');
}
