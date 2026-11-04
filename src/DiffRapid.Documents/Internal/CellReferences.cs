using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffRapid.Documents.Internal;

/// <summary>A1-style cell reference helpers.</summary>
internal static class CellReferences
{
    private const int MaxColumn = 16384;
    private const int MaxRow = 1048576;

    private static readonly Regex Reference = new(
        @"\G(\$?)([A-Za-z]{1,3})(\$?)([0-9]{1,7})(?![A-Za-z0-9_(!.])",
        RegexOptions.CultureInvariant);

    /// <summary>Returns the 1-based column of a reference such as "AB12", or null when it has no column letters.</summary>
    public static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        foreach (var character in reference!)
        {
            var upper = char.ToUpperInvariant(character);
            if (upper is < 'A' or > 'Z')
            {
                break;
            }

            column = (column * 26) + (upper - 'A' + 1);
            if (column > MaxColumn)
            {
                return null;
            }
        }

        return column == 0 ? null : column;
    }

    /// <summary>Returns the column letters for a 1-based column number.</summary>
    public static string ColumnName(int column)
    {
        var name = new StringBuilder();
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            name.Insert(0, (char)('A' + remainder));
            column = (column - 1) / 26;
        }

        return name.ToString();
    }

    /// <summary>
    /// Moves the relative cell references in a formula by the given rows and columns, as Excel does when it
    /// shares one formula across a range. Text in double quotes and quoted sheet names are left alone;
    /// whole-row and whole-column references are not shifted.
    /// </summary>
    public static string ShiftFormula(string formula, int rowDelta, int columnDelta)
    {
        var result = new StringBuilder(formula.Length + 8);
        var index = 0;
        while (index < formula.Length)
        {
            var character = formula[index];
            if (character is '"' or '\'')
            {
                index = CopyQuoted(formula, index, result);
                continue;
            }

            if (StartsToken(formula, index) && Reference.Match(formula, index) is { Success: true } match)
            {
                result.Append(Shift(match, rowDelta, columnDelta));
                index += match.Length;
                continue;
            }

            result.Append(character);
            index++;
        }

        return result.ToString();
    }

    private static bool StartsToken(string formula, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = formula[index - 1];
        return !(char.IsLetterOrDigit(previous) || previous is '_' or '.' or '$');
    }

    private static int CopyQuoted(string formula, int start, StringBuilder result)
    {
        var quote = formula[start];
        var index = start + 1;
        while (index < formula.Length)
        {
            if (formula[index] == quote)
            {
                // A doubled quote is an escaped quote inside the text.
                if (index + 1 < formula.Length && formula[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }

                index++;
                break;
            }

            index++;
        }

        result.Append(formula, start, index - start);
        return index;
    }

    private static string Shift(Match match, int rowDelta, int columnDelta)
    {
        var columnAbsolute = match.Groups[1].Value;
        var rowAbsolute = match.Groups[3].Value;
        var column = ColumnIndex(match.Groups[2].Value);
        var row = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        if (column is null || row < 1 || row > MaxRow)
        {
            return match.Value;
        }

        var newColumn = columnAbsolute.Length > 0 ? column.Value : column.Value + columnDelta;
        var newRow = rowAbsolute.Length > 0 ? row : row + rowDelta;
        if (newColumn < 1 || newColumn > MaxColumn || newRow < 1 || newRow > MaxRow)
        {
            return "#REF!";
        }

        return columnAbsolute + ColumnName(newColumn) + rowAbsolute + newRow.ToString(CultureInfo.InvariantCulture);
    }
}
