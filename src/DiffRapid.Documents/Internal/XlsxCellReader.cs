using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DiffRapid.Documents.Internal;

/// <summary>How a number format shows a serial date number.</summary>
internal enum DateKind
{
    None,
    Date,
    DateTime,
    Time,
}

/// <summary>Formats cells of one workbook: shared strings, number formats, dates and formulas.</summary>
internal sealed class XlsxCellReader
{
    private const double MaxSerialDate = 2958465.0;
    private const int Date1904Offset = 1462;

    private readonly string[] _sharedStrings;
    private readonly DateKind[] _styleDateKinds;
    private readonly bool _date1904;
    private readonly XlsxCellContent _content;

    public XlsxCellReader(WorkbookPart workbookPart, XlsxCellContent content)
    {
        _content = content;
        _sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => RichText(item))
            .ToArray() ?? [];
        _styleDateKinds = ReadStyleDateKinds(workbookPart.WorkbookStylesPart?.Stylesheet);
        _date1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value ?? false;
    }

    /// <summary>Returns the cell's text for the configured content: value, formula, or both.</summary>
    public string Read(Cell cell, int row, int column, Dictionary<uint, SharedFormula> sharedFormulas)
    {
        var value = Value(cell);
        if (_content == XlsxCellContent.Values)
        {
            return value;
        }

        var formula = Formula(cell.CellFormula, row, column, sharedFormulas);
        if (formula is null)
        {
            return value;
        }

        return _content == XlsxCellContent.Formulas ? "=" + formula : $"={formula} -> {value}";
    }

    /// <summary>Classifies a number format code by the date and time parts it shows.</summary>
    public static DateKind ClassifyFormat(string code)
    {
        var parts = new StringBuilder();
        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            if (character == ';')
            {
                break;
            }

            if (character == '"')
            {
                var close = code.IndexOf('"', index + 1);
                index = close < 0 ? code.Length : close;
                continue;
            }

            if (character is '\\' or '_' or '*')
            {
                index++;
                continue;
            }

            if (character == '[')
            {
                var close = code.IndexOf(']', index + 1);
                var inside = close < 0 ? string.Empty : code.Substring(index + 1, close - index - 1).ToLowerInvariant();
                if (inside.Length > 0 && inside.All(letter => letter is 'h' or 'm' or 's'))
                {
                    parts.Append(inside[0]);
                }

                index = close < 0 ? code.Length : close;
                continue;
            }

            parts.Append(char.ToLowerInvariant(character));
        }

        var letters = parts.ToString();
        if (letters.Contains("general"))
        {
            return DateKind.None;
        }

        var hasDate = letters.IndexOfAny(['y', 'd']) >= 0;
        var hasTime = letters.IndexOfAny(['h', 's']) >= 0;
        if (hasDate && hasTime)
        {
            return DateKind.DateTime;
        }

        if (hasTime)
        {
            return DateKind.Time;
        }

        return hasDate || letters.IndexOf('m') >= 0 ? DateKind.Date : DateKind.None;
    }

    /// <summary>Returns how a built-in number format id shows dates.</summary>
    public static DateKind ClassifyBuiltInFormat(uint id) => id switch
    {
        >= 14 and <= 17 => DateKind.Date,
        22 => DateKind.DateTime,
        >= 18 and <= 21 => DateKind.Time,
        >= 45 and <= 47 => DateKind.Time,
        >= 27 and <= 36 => DateKind.Date,
        >= 50 and <= 58 => DateKind.Date,
        _ => DateKind.None,
    };

    /// <summary>Formats a serial date number in ISO 8601 form; null when it is out of range.</summary>
    public static string? FormatSerialDate(double serial, DateKind kind, bool date1904)
    {
        if (date1904)
        {
            serial += Date1904Offset;
        }

        if (kind == DateKind.Time)
        {
            var seconds = (long)Math.Round(Math.Abs(serial) * 86400.0);
            var hours = seconds / 3600;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", hours, seconds / 60 % 60, seconds % 60);
        }

        if (serial < 0 || serial >= MaxSerialDate)
        {
            return null;
        }

        var days = Math.Floor(serial);
        var timeOfDay = Math.Round((serial - days) * 86400.0);
        var moment = new DateTime(1899, 12, 30).AddDays(days).AddSeconds(timeOfDay);
        return kind == DateKind.Date
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private string Value(Cell cell)
    {
        var type = cell.DataType?.Value;
        var raw = cell.CellValue?.Text;
        if (type == CellValues.InlineString)
        {
            return cell.InlineString is null ? raw ?? string.Empty : RichText(cell.InlineString);
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < _sharedStrings.Length
                ? _sharedStrings[index]
                : raw;
        }

        if (type == CellValues.Boolean)
        {
            return raw.Trim() is "1" or "true" ? "TRUE" : "FALSE";
        }

        if (type is not null && type != CellValues.Number)
        {
            return raw;
        }

        return FormatNumber(raw, cell.StyleIndex?.Value);
    }

    private string FormatNumber(string raw, uint? styleIndex)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }

        var kind = styleIndex is { } index && index < _styleDateKinds.Length ? _styleDateKinds[index] : DateKind.None;
        if (kind != DateKind.None && FormatSerialDate(number, kind, _date1904) is { } date)
        {
            return date;
        }

        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string? Formula(CellFormula? formula, int row, int column, Dictionary<uint, SharedFormula> sharedFormulas)
    {
        if (formula is null)
        {
            return null;
        }

        var text = formula.Text;
        var isShared = formula.FormulaType?.Value == CellFormulaValues.Shared;
        if (!isShared || formula.SharedIndex?.Value is not { } sharedIndex)
        {
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (!string.IsNullOrEmpty(text))
        {
            sharedFormulas[sharedIndex] = new SharedFormula(text, row, column);
            return text;
        }

        return sharedFormulas.TryGetValue(sharedIndex, out var anchor)
            ? CellReferences.ShiftFormula(anchor.Text, row - anchor.Row, column - anchor.Column)
            : null;
    }

    /// <summary>The visible text of a shared or inline string: plain text plus rich-text runs, without phonetic hints.</summary>
    private static string RichText(OpenXmlElement item)
    {
        var text = new StringBuilder();
        foreach (var child in item.ChildElements)
        {
            if (child is Text plain)
            {
                text.Append(plain.Text);
            }
            else if (child is Run run && run.Text is { } runText)
            {
                text.Append(runText.Text);
            }
        }

        return text.ToString();
    }

    private static DateKind[] ReadStyleDateKinds(Stylesheet? stylesheet)
    {
        var custom = new Dictionary<uint, DateKind>();
        foreach (var format in stylesheet?.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id)
            {
                custom[id] = ClassifyFormat(format.FormatCode?.Value ?? string.Empty);
            }
        }

        return stylesheet?.CellFormats?.Elements<CellFormat>()
            .Select(cellFormat => cellFormat.NumberFormatId?.Value is { } id
                ? custom.TryGetValue(id, out var kind) ? kind : ClassifyBuiltInFormat(id)
                : DateKind.None)
            .ToArray() ?? [];
    }
}
