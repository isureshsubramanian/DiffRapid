using System;
using System.Collections.Generic;
using System.Text;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Table;

/// <summary>How a delimited text file is split into rows and cells.</summary>
public sealed record DelimitedReadOptions
{
    public static readonly DelimitedReadOptions Default = new();

    /// <summary>The cell separator; null detects comma, tab, semicolon or pipe from the first lines.</summary>
    public char? Delimiter { get; init; }

    /// <summary>The character that encloses cells containing delimiters, quotes or line breaks.</summary>
    public char Quote { get; init; } = '"';

    /// <summary>Treat the first record as column names.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Encoding used when the file has no byte order mark and is not valid UTF-8.</summary>
    public Encoding? FallbackEncoding { get; init; }
}

/// <summary>
/// Reads CSV (RFC 4180), TSV and other single-character delimited text. Quoted cells may contain the
/// delimiter, line breaks and doubled quotes; rows may have different lengths; empty lines are skipped.
/// </summary>
public static class DelimitedReader
{
    /// <summary>Delimiters tried by auto-detection, in order of preference when equally likely.</summary>
    public static readonly IReadOnlyList<char> CandidateDelimiters = [',', '\t', ';', '|'];

    private const int DetectionLineCount = 20;

    /// <summary>Reads a file, detecting its encoding from the byte order mark or content.</summary>
    public static TableData ReadFile(string path, DelimitedReadOptions? options = null)
    {
        options ??= DelimitedReadOptions.Default;
        return Parse(TextFileReader.ReadFile(path, options.FallbackEncoding).Text, options);
    }

    /// <summary>Splits text into a table.</summary>
    public static TableData Parse(string text, DelimitedReadOptions? options = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        options ??= DelimitedReadOptions.Default;
        var delimiter = options.Delimiter ?? DetectDelimiter(text, options.Quote);
        if (delimiter == options.Quote || delimiter is '\r' or '\n')
        {
            throw new ArgumentException("The delimiter cannot be the quote character or a line break.", nameof(options));
        }

        var rows = new List<IReadOnlyList<string>>();
        var lines = new List<int>();
        IReadOnlyList<string>? header = null;
        foreach (var (record, line) in ReadRecords(text, delimiter, options.Quote))
        {
            if (options.HasHeader && header is null)
            {
                header = record;
                continue;
            }

            rows.Add(record);
            lines.Add(line);
        }

        if (options.HasHeader && header is null)
        {
            header = Array.Empty<string>();
        }

        return new TableData(header, rows, delimiter, lines);
    }

    /// <summary>
    /// Picks the candidate delimiter that splits the first lines into the same, largest number of cells.
    /// Falls back to a comma.
    /// </summary>
    public static char DetectDelimiter(string text, char quote = '"')
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var best = ',';
        var bestScore = 0L;
        foreach (var candidate in CandidateDelimiters)
        {
            var score = Score(text, candidate, quote);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    // Lines that agree with the first line's cell count weigh most; wider splits break ties.
    private static long Score(string text, char delimiter, char quote)
    {
        var counts = new List<int>();
        foreach (var (record, _) in ReadRecords(text, delimiter, quote))
        {
            counts.Add(record.Count - 1);
            if (counts.Count == DetectionLineCount)
            {
                break;
            }
        }

        if (counts.Count == 0 || counts[0] == 0)
        {
            return 0;
        }

        var consistent = 0;
        foreach (var count in counts)
        {
            if (count == counts[0])
            {
                consistent++;
            }
        }

        return (long)consistent * 1000 + Math.Min(counts[0], 999);
    }

    /// <summary>Yields each record with the one-based line it starts on.</summary>
    internal static IEnumerable<(IReadOnlyList<string> Record, int Line)> ReadRecords(string text, char delimiter, char quote)
    {
        var parser = new RecordParser(text, delimiter, quote);
        while (parser.TryRead(out var record, out var line))
        {
            yield return (record, line);
        }
    }

    private sealed class RecordParser
    {
        private readonly string _text;
        private readonly char _delimiter;
        private readonly char _quote;
        private readonly StringBuilder _cell = new();
        private int _position;
        private int _line = 1;

        public RecordParser(string text, char delimiter, char quote)
        {
            _text = text;
            _delimiter = delimiter;
            _quote = quote;
        }

        public bool TryRead(out IReadOnlyList<string> record, out int line)
        {
            SkipEmptyLines();
            line = _line;
            if (_position >= _text.Length)
            {
                record = Array.Empty<string>();
                return false;
            }

            var cells = new List<string>();
            while (true)
            {
                var ended = ReadCell(out var cell);
                cells.Add(cell);
                if (ended)
                {
                    break;
                }
            }

            record = cells;
            return true;
        }

        private void SkipEmptyLines()
        {
            while (_position < _text.Length && IsLineBreak(_text[_position]))
            {
                ConsumeLineBreak();
            }
        }

        // Reads one cell; returns true when it was the last cell of the record.
        private bool ReadCell(out string cell)
        {
            _cell.Clear();
            var quoted = _position < _text.Length && _text[_position] == _quote;
            if (quoted)
            {
                _position++;
                ReadQuotedPart();
            }

            // Unquoted text, or stray text after a closing quote, runs to the next delimiter or line break.
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c == _delimiter)
                {
                    _position++;
                    cell = _cell.ToString();
                    return false;
                }

                if (IsLineBreak(c))
                {
                    ConsumeLineBreak();
                    break;
                }

                _cell.Append(c);
                _position++;
            }

            cell = _cell.ToString();
            return true;
        }

        // Reads up to and past the closing quote; a doubled quote is one literal quote.
        private void ReadQuotedPart()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c == _quote)
                {
                    var doubled = _position + 1 < _text.Length && _text[_position + 1] == _quote;
                    if (!doubled)
                    {
                        _position++;
                        return;
                    }

                    _cell.Append(_quote);
                    _position += 2;
                    continue;
                }

                if (c == '\n' || (c == '\r' && (_position + 1 >= _text.Length || _text[_position + 1] != '\n')))
                {
                    _line++;
                }

                _cell.Append(c);
                _position++;
            }
        }

        private void ConsumeLineBreak()
        {
            if (_text[_position] == '\r' && _position + 1 < _text.Length && _text[_position + 1] == '\n')
            {
                _position++;
            }

            _position++;
            _line++;
        }

        private static bool IsLineBreak(char c) => c is '\r' or '\n';
    }
}
