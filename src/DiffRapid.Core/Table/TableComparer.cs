using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using CompareOptions = DiffRapid.Core.Comparison.CompareOptions;

namespace DiffRapid.Core.Table;

/// <summary>
/// Compares two tables row by row and cell by cell. Rows are aligned with a line diff over whole rows, or
/// matched by key columns when <see cref="TableCompareOptions.KeyColumns"/> is set.
/// </summary>
public static class TableComparer
{
    private const char CellSeparator = '\u001f';
    private const char OccurrenceSeparator = '\u001e';

    public static TableDiffResult Compare(
        TableData left,
        TableData right,
        TableCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options ??= TableCompareOptions.Default;
        if (options.AbsoluteTolerance < 0 || options.RelativeTolerance < 0)
        {
            throw new ArgumentException("Tolerances cannot be negative.", nameof(options));
        }

        var session = new Session(left, right, options, cancellationToken);
        return session.Run();
    }

    /// <summary>
    /// Suggests a key column for two tables with headers: a column both tables have whose values are present and
    /// unique on each side, and at least half of whose values appear on both sides (so it identifies the same rows,
    /// not merely distinct ones). A column named like an identifier (Id, Key, Code, Number, or ending in Id) wins;
    /// otherwise the first such column. Null when the tables have no header or no column qualifies.
    /// </summary>
    public static string? SuggestKey(TableData left, TableData right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (left.Header is null || right.Header is null || left.Rows.Count == 0 || right.Rows.Count == 0)
        {
            return null;
        }

        string? first = null;
        for (var l = 0; l < left.Header.Count; l++)
        {
            var name = left.Header[l];
            var r = IndexOf(right.Header, name);
            if (r < 0 || !IsKey(left, l, right, r))
            {
                continue;
            }

            if (LooksLikeIdentifier(name))
            {
                return name;
            }

            first ??= name;
        }

        return first;
    }

    private static int IndexOf(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsKey(TableData left, int leftColumn, TableData right, int rightColumn)
    {
        var leftValues = UniqueValues(left, leftColumn);
        var rightValues = UniqueValues(right, rightColumn);
        if (leftValues is null || rightValues is null)
        {
            return false;
        }

        var shared = leftValues.Count(rightValues.Contains);
        return shared * 2 >= Math.Min(leftValues.Count, rightValues.Count);
    }

    // The column's values when every row has one and none repeats; otherwise null.
    private static HashSet<string>? UniqueValues(TableData table, int column)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < table.Rows.Count; row++)
        {
            var value = table.Cell(row, column).Trim();
            if (value.Length == 0 || !values.Add(value))
            {
                return null;
            }
        }

        return values;
    }

    private static bool LooksLikeIdentifier(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Equals("Key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Code", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Number", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("No", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("Key", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Session
    {
        private readonly TableData _left;
        private readonly TableData _right;
        private readonly TableCompareOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly List<TableColumn> _columns;
        private readonly int[] _compared;
        private readonly int[] _keys;
        private readonly CompareOptions _inlineOptions;
        private readonly List<TableRowDiff> _rows = [];
        private readonly List<TableDuplicateKey> _duplicates = [];

        public Session(TableData left, TableData right, TableCompareOptions options, CancellationToken cancellationToken)
        {
            _left = left;
            _right = right;
            _options = options;
            _cancellationToken = cancellationToken;
            _columns = TableColumnMap.Build(left, right, options);
            _compared = Enumerable.Range(0, _columns.Count).Where(i => _columns[i].IsCompared).ToArray();
            _keys = Enumerable.Range(0, _columns.Count).Where(i => _columns[i].IsKey).ToArray();
            _inlineOptions = new CompareOptions
            {
                IgnoreCase = options.IgnoreCase,
                Whitespace = options.TrimWhitespace ? WhitespaceMode.IgnoreChange : WhitespaceMode.Compare,
            };
        }

        public TableDiffResult Run()
        {
            if (_keys.Length > 0)
            {
                MatchByKey();
            }
            else
            {
                MatchByPosition();
            }

            return new TableDiffResult(_left, _right, _columns, _rows, _duplicates);
        }

        // Rows are aligned by a line diff over the compared cells; changed runs are paired in order.
        private void MatchByPosition()
        {
            var interned = new Dictionary<string, int>(StringComparer.Ordinal);
            var leftKeys = RowKeys(_left, isLeft: true, _compared, interned);
            var rightKeys = RowKeys(_right, isLeft: false, _compared, interned);
            var changedLeft = new bool[leftKeys.Length];
            var changedRight = new bool[rightKeys.Length];
            DiffAlgorithms.Create(_options.Algorithm).Compare(leftKeys, rightKeys, changedLeft, changedRight, _cancellationToken);

            var moves = _options.DetectMovedRows
                ? FindMoves(leftKeys, rightKeys, changedLeft, changedRight)
                : new Dictionary<int, int>();
            var movedRight = new HashSet<int>(moves.Values);
            foreach (var (leftStart, leftEnd, rightStart, rightEnd) in Walk(changedLeft, changedRight))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var isCommon = leftEnd - leftStart == 1 && rightEnd - rightStart == 1 && !changedLeft[leftStart] && !changedRight[rightStart];
                if (isCommon)
                {
                    _rows.Add(new TableRowDiff(TableRowStatus.Same, leftStart, rightStart, Array.Empty<TableCellDiff>()));
                    continue;
                }

                EmitPositionalHunk(leftStart, leftEnd, rightStart, rightEnd, moves, movedRight);
            }
        }

        private void EmitPositionalHunk(int leftStart, int leftEnd, int rightStart, int rightEnd, Dictionary<int, int> moves, HashSet<int> movedRight)
        {
            var lefts = new List<int>();
            for (var i = leftStart; i < leftEnd; i++)
            {
                if (!moves.ContainsKey(i))
                {
                    lefts.Add(i);
                }
            }

            var rights = new List<int>();
            for (var j = rightStart; j < rightEnd; j++)
            {
                if (!movedRight.Contains(j))
                {
                    rights.Add(j);
                }
            }

            var paired = 0;
            for (var i = leftStart; i < leftEnd; i++)
            {
                if (moves.TryGetValue(i, out var target))
                {
                    _rows.Add(Paired(i, target, moved: true));
                    continue;
                }

                if (paired < rights.Count)
                {
                    _rows.Add(Paired(i, rights[paired], moved: false));
                    paired++;
                    continue;
                }

                _rows.Add(new TableRowDiff(TableRowStatus.LeftOnly, i, null, Array.Empty<TableCellDiff>()));
            }

            for (var k = paired; k < rights.Count; k++)
            {
                _rows.Add(new TableRowDiff(TableRowStatus.RightOnly, null, rights[k], Array.Empty<TableCellDiff>()));
            }
        }

        // Pairs changed left rows with changed right rows that have exactly the same content elsewhere.
        private static Dictionary<int, int> FindMoves(int[] leftKeys, int[] rightKeys, bool[] changedLeft, bool[] changedRight)
        {
            var waiting = new Dictionary<int, Queue<int>>();
            for (var j = 0; j < rightKeys.Length; j++)
            {
                if (!changedRight[j])
                {
                    continue;
                }

                if (!waiting.TryGetValue(rightKeys[j], out var queue))
                {
                    queue = new Queue<int>();
                    waiting.Add(rightKeys[j], queue);
                }

                queue.Enqueue(j);
            }

            var moves = new Dictionary<int, int>();
            for (var i = 0; i < leftKeys.Length; i++)
            {
                if (changedLeft[i] && waiting.TryGetValue(leftKeys[i], out var queue) && queue.Count > 0)
                {
                    moves.Add(i, queue.Dequeue());
                }
            }

            return moves;
        }

        // Rows are matched by key value; the n-th occurrence of a duplicate key matches the n-th on the other side.
        private void MatchByKey()
        {
            var leftTokens = KeyTokens(_left, isLeft: true, TableSide.Left);
            var rightTokens = KeyTokens(_right, isLeft: false, TableSide.Right);
            var interned = new Dictionary<string, int>(StringComparer.Ordinal);
            var leftIds = Intern(leftTokens, interned);
            var rightIds = Intern(rightTokens, interned);
            var rightByToken = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < rightTokens.Length; j++)
            {
                rightByToken.Add(rightTokens[j], j);
            }

            var leftByToken = new HashSet<string>(leftTokens, StringComparer.Ordinal);
            var changedLeft = new bool[leftIds.Length];
            var changedRight = new bool[rightIds.Length];
            DiffAlgorithms.Create(DiffAlgorithmKind.Histogram).Compare(leftIds, rightIds, changedLeft, changedRight, _cancellationToken);

            foreach (var (leftStart, leftEnd, rightStart, rightEnd) in Walk(changedLeft, changedRight))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                for (var i = leftStart; i < leftEnd; i++)
                {
                    if (rightByToken.TryGetValue(leftTokens[i], out var match))
                    {
                        _rows.Add(Paired(i, match, moved: changedLeft[i] && _options.DetectMovedRows));
                    }
                    else
                    {
                        _rows.Add(new TableRowDiff(TableRowStatus.LeftOnly, i, null, Array.Empty<TableCellDiff>()));
                    }
                }

                for (var j = rightStart; j < rightEnd; j++)
                {
                    if (changedRight[j] && !leftByToken.Contains(rightTokens[j]))
                    {
                        _rows.Add(new TableRowDiff(TableRowStatus.RightOnly, null, j, Array.Empty<TableCellDiff>()));
                    }
                }
            }
        }

        // One token per row: the normalized key plus its occurrence number, so duplicates stay distinct.
        private string[] KeyTokens(TableData table, bool isLeft, TableSide side)
        {
            var tokens = new string[table.Rows.Count];
            var seen = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var row = 0; row < tokens.Length; row++)
            {
                var key = JoinCells(table, row, isLeft, _keys);
                if (!seen.TryGetValue(key, out var rows))
                {
                    rows = [];
                    seen.Add(key, rows);
                }

                tokens[row] = key + OccurrenceSeparator + rows.Count.ToString(CultureInfo.InvariantCulture);
                rows.Add(row);
            }

            foreach (var rows in seen.Values.Where(r => r.Count > 1))
            {
                var values = _keys.Select(k => Cell(table, rows[0], isLeft, k)).ToList();
                _duplicates.Add(new TableDuplicateKey(side, values, rows));
            }

            return tokens;
        }

        private int[] RowKeys(TableData table, bool isLeft, int[] columns, Dictionary<string, int> interned)
        {
            var keys = new int[table.Rows.Count];
            for (var row = 0; row < keys.Length; row++)
            {
                var key = JoinCells(table, row, isLeft, columns);
                if (!interned.TryGetValue(key, out var id))
                {
                    id = interned.Count;
                    interned.Add(key, id);
                }

                keys[row] = id;
            }

            return keys;
        }

        private static int[] Intern(string[] tokens, Dictionary<string, int> interned)
        {
            var ids = new int[tokens.Length];
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!interned.TryGetValue(tokens[i], out var id))
                {
                    id = interned.Count;
                    interned.Add(tokens[i], id);
                }

                ids[i] = id;
            }

            return ids;
        }

        private string JoinCells(TableData table, int row, bool isLeft, int[] columns)
        {
            var parts = new string[columns.Length];
            for (var k = 0; k < columns.Length; k++)
            {
                parts[k] = Normalize(Cell(table, row, isLeft, columns[k]));
            }

            return string.Join(CellSeparator.ToString(), parts);
        }

        private TableRowDiff Paired(int leftRow, int rightRow, bool moved)
        {
            var cells = new List<TableCellDiff>(_compared.Length);
            var changed = false;
            foreach (var column in _compared)
            {
                var leftText = Cell(_left, leftRow, isLeft: true, column);
                var rightText = Cell(_right, rightRow, isLeft: false, column);
                var equal = CellsEqual(leftText, rightText);
                var inline = equal ? null : InlineDiffer.Compare(leftText, rightText, _inlineOptions);
                cells.Add(new TableCellDiff(column, leftText, rightText, !equal, inline));
                changed |= !equal;
            }

            if (moved)
            {
                return new TableRowDiff(TableRowStatus.Moved, leftRow, rightRow, cells);
            }

            if (!changed)
            {
                return new TableRowDiff(TableRowStatus.Same, leftRow, rightRow, Array.Empty<TableCellDiff>());
            }

            return new TableRowDiff(TableRowStatus.Changed, leftRow, rightRow, cells);
        }

        private string Cell(TableData table, int row, bool isLeft, int column)
        {
            var index = isLeft ? _columns[column].LeftIndex : _columns[column].RightIndex;
            return index is { } i ? table.Cell(row, i) : string.Empty;
        }

        private string Normalize(string value)
        {
            var text = _options.TrimWhitespace ? value.Trim() : value;
            return _options.IgnoreCase ? text.ToUpperInvariant() : text;
        }

        private bool CellsEqual(string left, string right)
        {
            if (string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal))
            {
                return true;
            }

            var tolerant = _options.AbsoluteTolerance > 0 || _options.RelativeTolerance > 0;
            if (!tolerant || !TryParseNumber(left, out var a) || !TryParseNumber(right, out var b))
            {
                return false;
            }

            var difference = Math.Abs(a - b);
            var withinAbsolute = difference <= _options.AbsoluteTolerance;
            var withinRelative = difference <= _options.RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b));
            return withinAbsolute || withinRelative;
        }

        private static bool TryParseNumber(string text, out double value)
        {
            var parsed = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return parsed && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// Splits an alignment into runs: each common element as a one-by-one run, and each block of changes as one
    /// run covering the changed elements on both sides.
    /// </summary>
    internal static IEnumerable<(int LeftStart, int LeftEnd, int RightStart, int RightEnd)> Walk(bool[] changedLeft, bool[] changedRight)
    {
        var i = 0;
        var j = 0;
        while (i < changedLeft.Length || j < changedRight.Length)
        {
            var bothCommon = i < changedLeft.Length && j < changedRight.Length && !changedLeft[i] && !changedRight[j];
            if (bothCommon)
            {
                yield return (i, i + 1, j, j + 1);
                i++;
                j++;
                continue;
            }

            var leftStart = i;
            var rightStart = j;
            while (i < changedLeft.Length && changedLeft[i])
            {
                i++;
            }

            while (j < changedRight.Length && changedRight[j])
            {
                j++;
            }

            if (i == leftStart && j == rightStart)
            {
                // The sides are out of step (one ran out); the rest of the longer side is changed.
                i = changedLeft.Length;
                j = changedRight.Length;
            }

            yield return (leftStart, i, rightStart, j);
        }
    }
}
