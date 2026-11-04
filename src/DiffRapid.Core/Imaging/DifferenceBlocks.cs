using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DiffRapid.Core.Imaging;

/// <summary>Groups differing pixels into grid blocks and the blocks into non-overlapping rectangles.</summary>
internal static class DifferenceBlocks
{
    /// <summary>
    /// Marks every <paramref name="blockSize"/> square cell holding a differing pixel, joins touching cells
    /// (diagonals included) into groups, and returns each group's bounding rectangle in pixels. Groups whose
    /// rectangles overlap are joined until no rectangles overlap.
    /// </summary>
    public static IReadOnlyList<ImageRegion> FindRegions(PixelDiffKind[] kinds, int width, int height, int blockSize, CancellationToken cancellationToken)
    {
        var columns = (width + blockSize - 1) / blockSize;
        var rows = (height + blockSize - 1) / blockSize;
        var marked = MarkCells(kinds, width, height, blockSize, columns, rows, cancellationToken);
        List<CellBox> boxes;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            boxes = FindGroups(marked, columns, rows);
        }
        while (FillBoxes(marked, columns, boxes));

        return boxes
            .Select(box => ToRegion(box, blockSize, width, height))
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToList();
    }

    private static bool[] MarkCells(PixelDiffKind[] kinds, int width, int height, int blockSize, int columns, int rows, CancellationToken cancellationToken)
    {
        var marked = new bool[columns * rows];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = (long)y * width;
            var cellRow = y / blockSize * columns;
            for (var x = 0; x < width; x++)
            {
                if (ImageDiffResult.IsDifference(kinds[rowStart + x]))
                {
                    marked[cellRow + x / blockSize] = true;
                }
            }
        }

        return marked;
    }

    /// <summary>Finds the 8-connected groups of marked cells and returns their bounding boxes.</summary>
    private static List<CellBox> FindGroups(bool[] marked, int columns, int rows)
    {
        var visited = new bool[marked.Length];
        var boxes = new List<CellBox>();
        var pending = new Stack<int>();
        for (var start = 0; start < marked.Length; start++)
        {
            if (!marked[start] || visited[start])
            {
                continue;
            }

            var box = new CellBox(start % columns, start / columns);
            visited[start] = true;
            pending.Push(start);
            while (pending.Count > 0)
            {
                var cell = pending.Pop();
                var column = cell % columns;
                var row = cell / columns;
                box = box.Include(column, row);
                VisitNeighbours(marked, visited, pending, columns, rows, column, row);
            }

            boxes.Add(box);
        }

        return boxes;
    }

    private static void VisitNeighbours(bool[] marked, bool[] visited, Stack<int> pending, int columns, int rows, int column, int row)
    {
        for (var neighbourRow = Math.Max(0, row - 1); neighbourRow <= Math.Min(rows - 1, row + 1); neighbourRow++)
        {
            for (var neighbourColumn = Math.Max(0, column - 1); neighbourColumn <= Math.Min(columns - 1, column + 1); neighbourColumn++)
            {
                var neighbour = neighbourRow * columns + neighbourColumn;
                if (marked[neighbour] && !visited[neighbour])
                {
                    visited[neighbour] = true;
                    pending.Push(neighbour);
                }
            }
        }
    }

    /// <summary>Marks every cell inside every box; returns true when that marked a new cell, so groups may join.</summary>
    private static bool FillBoxes(bool[] marked, int columns, List<CellBox> boxes)
    {
        var changed = false;
        foreach (var box in boxes)
        {
            for (var row = box.Top; row <= box.Bottom; row++)
            {
                for (var column = box.Left; column <= box.Right; column++)
                {
                    var cell = row * columns + column;
                    if (!marked[cell])
                    {
                        marked[cell] = true;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private static ImageRegion ToRegion(CellBox box, int blockSize, int width, int height)
    {
        var x = box.Left * blockSize;
        var y = box.Top * blockSize;
        var right = Math.Min(width, (box.Right + 1) * blockSize);
        var bottom = Math.Min(height, (box.Bottom + 1) * blockSize);
        return new ImageRegion(x, y, right - x, bottom - y);
    }

    /// <summary>An inclusive box of grid cells.</summary>
    private readonly record struct CellBox(int Left, int Top, int Right, int Bottom)
    {
        public CellBox(int column, int row)
            : this(column, row, column, row)
        {
        }

        public CellBox Include(int column, int row) => new(Math.Min(Left, column), Math.Min(Top, row), Math.Max(Right, column), Math.Max(Bottom, row));
    }
}
