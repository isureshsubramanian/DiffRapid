using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiffRapid.Core.Table;

/// <summary>Pairs the columns of two tables and resolves column references from the options.</summary>
internal static class TableColumnMap
{
    public static List<TableColumn> Build(TableData left, TableData right, TableCompareOptions options)
    {
        var byName = options.MatchColumnsByName && left.Header is { Count: > 0 } && right.Header is { Count: > 0 };
        var pairs = byName ? PairByName(left, right, options) : PairByPosition(left, right);
        var columns = pairs
            .Select(p => new TableColumn(
                NameOf(left, right, p.Left, p.Right), p.Left, p.Right,
                HeaderName(left, p.Left), HeaderName(right, p.Right), IsKey: false, IsIgnored: false))
            .ToList();

        foreach (var reference in options.IgnoreColumns)
        {
            var index = Resolve(columns, reference, options);
            columns[index] = columns[index] with { IsIgnored = true };
        }

        foreach (var reference in options.KeyColumns)
        {
            var index = Resolve(columns, reference, options);
            if (columns[index].LeftIndex is null || columns[index].RightIndex is null)
            {
                throw new ArgumentException($"Key column '{reference}' is not in both tables.", nameof(options));
            }

            columns[index] = columns[index] with { IsKey = true };
        }

        return columns;
    }

    /// <summary>Finds a column by header name, then by one-based column number.</summary>
    public static int Resolve(IReadOnlyList<TableColumn> columns, string reference, TableCompareOptions options)
    {
        var comparison = NameComparison(options);
        var wanted = Clean(reference, options);
        for (var i = 0; i < columns.Count; i++)
        {
            var leftMatch = columns[i].LeftName is { } leftName && string.Equals(Clean(leftName, options), wanted, comparison);
            var rightMatch = columns[i].RightName is { } rightName && string.Equals(Clean(rightName, options), wanted, comparison);
            if (leftMatch || rightMatch)
            {
                return i;
            }
        }

        var isNumber = int.TryParse(reference.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number);
        if (isNumber && number >= 1)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var position = columns[i].LeftIndex ?? columns[i].RightIndex;
                if (position == number - 1)
                {
                    return i;
                }
            }
        }

        throw new ArgumentException($"Unknown column '{reference}'.", nameof(reference));
    }

    private static List<(int? Left, int? Right)> PairByPosition(TableData left, TableData right)
    {
        var count = Math.Max(left.ColumnCount, right.ColumnCount);
        var pairs = new List<(int? Left, int? Right)>(count);
        for (var i = 0; i < count; i++)
        {
            pairs.Add((i < left.ColumnCount ? i : null, i < right.ColumnCount ? i : null));
        }

        return pairs;
    }

    // Named columns pair with the first unused right column of the same name; unnamed ones pair by position.
    private static List<(int? Left, int? Right)> PairByName(TableData left, TableData right, TableCompareOptions options)
    {
        var comparison = NameComparison(options);
        var used = new bool[right.ColumnCount];
        var pairs = new List<(int? Left, int? Right)>();
        for (var i = 0; i < left.ColumnCount; i++)
        {
            var name = HeaderName(left, i);
            int? match = null;
            for (var j = 0; j < right.ColumnCount && match is null; j++)
            {
                var other = HeaderName(right, j);
                var bothUnnamed = string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(other) && i == j;
                var sameName = !string.IsNullOrWhiteSpace(name) && other is not null
                    && string.Equals(Clean(name!, options), Clean(other, options), comparison);
                if (!used[j] && (sameName || bothUnnamed))
                {
                    match = j;
                }
            }

            if (match is { } found)
            {
                used[found] = true;
            }

            pairs.Add((i, match));
        }

        for (var j = 0; j < right.ColumnCount; j++)
        {
            if (!used[j])
            {
                pairs.Add((null, j));
            }
        }

        return pairs;
    }

    private static string? HeaderName(TableData table, int? index)
    {
        if (index is not { } i || table.Header is null || i >= table.Header.Count)
        {
            return null;
        }

        return table.Header[i];
    }

    private static string NameOf(TableData left, TableData right, int? leftIndex, int? rightIndex)
    {
        var name = HeaderName(left, leftIndex) ?? HeaderName(right, rightIndex);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name!;
        }

        var position = (leftIndex ?? rightIndex ?? 0) + 1;
        return "Column " + position.ToString(CultureInfo.InvariantCulture);
    }

    private static string Clean(string name, TableCompareOptions options) => options.TrimWhitespace ? name.Trim() : name;

    private static StringComparison NameComparison(TableCompareOptions options) =>
        options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
