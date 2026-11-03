using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiffRapid.Core.Comparison;

/// <summary>A regular expression identifying changed lines that may be ignored.</summary>
public sealed record LineFilter(string Pattern, bool Enabled = true);

/// <summary>A replacement applied to line comparison keys without changing displayed text.</summary>
public sealed record SubstitutionFilter(string Find, string Replace, bool IsRegex, bool CaseSensitive, bool WholeWord, bool Enabled = true);

internal sealed class FilterList<T> : IReadOnlyList<T>, IEquatable<FilterList<T>>
{
    private readonly T[] _items;

    public FilterList(IReadOnlyList<T> items) => _items = items.ToArray();

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(FilterList<T>? other) => other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is FilterList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items)
        {
            hash = unchecked(hash * 31 + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item)));
        }

        return hash;
    }
}

internal static class FilterRegex
{
    public static Regex Create(string pattern, bool caseSensitive = true, string? originalPattern = null)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant |
                (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid filter pattern '{originalPattern ?? pattern}'.", nameof(pattern), ex);
        }
    }
}
