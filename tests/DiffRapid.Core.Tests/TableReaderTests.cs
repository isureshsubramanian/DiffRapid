using System.Text;
using DiffRapid.Core.Table;

namespace DiffRapid.Core.Tests;

public sealed class TableReaderTests
{
    private static readonly DelimitedReadOptions NoHeader = new() { HasHeader = false };

    [Fact]
    public void Quoted_cells_keep_delimiters_doubled_quotes_and_empty_values()
    {
        var table = DelimitedReader.Parse("a,\"b,c\",\"say \"\"hi\"\"\",,\"\"\r\n", NoHeader with { Delimiter = ',' });

        Assert.Equal(["a", "b,c", "say \"hi\"", "", ""], table.Rows[0]);
    }

    [Fact]
    public void Quoted_cells_can_span_lines_and_line_numbers_follow_the_file()
    {
        var table = DelimitedReader.Parse("id,note\r\n1,\"first\r\nsecond\nthird\"\r\n2,x\n", new DelimitedReadOptions { Delimiter = ',' });

        Assert.Equal(["id", "note"], table.Header!);
        Assert.Equal("first\r\nsecond\nthird", table.Rows[0][1]);
        Assert.Equal([2, 5], table.LineNumbers!);
    }

    [Fact]
    public void Ragged_rows_blank_lines_and_missing_final_newline_are_allowed()
    {
        var table = DelimitedReader.Parse("a,b,c\n\n1\n\r\n1,2,3,4", NoHeader with { Delimiter = ',' });

        Assert.Equal(3, table.Rows.Count);
        Assert.Single(table.Rows[1]);
        Assert.Equal(4, table.ColumnCount);
        Assert.Equal(string.Empty, table.Cell(1, 2));
    }

    [Fact]
    public void Stray_text_after_a_closing_quote_and_quotes_inside_unquoted_cells_are_kept()
    {
        var table = DelimitedReader.Parse("\"ab\"cd,e\"f\"g,\"unterminated", NoHeader with { Delimiter = ',' });

        Assert.Equal(["abcd", "e\"f\"g", "unterminated"], table.Rows[0]);
    }

    [Fact]
    public void A_custom_quote_character_and_delimiter_are_honoured()
    {
        var options = NoHeader with { Delimiter = '^', Quote = '\'' };
        var table = DelimitedReader.Parse("'a^b'^'it''s'^\"x\"", options);

        Assert.Equal(["a^b", "it's", "\"x\""], table.Rows[0]);
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n", ',')]
    [InlineData("a\tb\tc\n1\t2,5\t3\n", '\t')]
    [InlineData("a;b;c\n1,5;2,5;3\n4,1;5;6\n", ';')]
    [InlineData("a|b\n1|2\n", '|')]
    [InlineData("\"x;y\",b\n\"1;2\",3\n", ',')]
    [InlineData("single column\nvalue\n", ',')]
    public void The_delimiter_is_detected_from_the_first_lines(string text, char expected)
    {
        Assert.Equal(expected, DelimitedReader.DetectDelimiter(text));
        Assert.Equal(expected, DelimitedReader.Parse(text).Delimiter);
    }

    [Fact]
    public void Files_are_decoded_with_their_byte_order_mark()
    {
        var path = Path.Combine(Path.GetTempPath(), "diffrapid-table-" + Guid.NewGuid() + ".tsv");
        try
        {
            File.WriteAllText(path, "naïve\tcafé\n1\t2\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var table = DelimitedReader.ReadFile(path);

            Assert.Equal('\t', table.Delimiter);
            Assert.Equal(["naïve", "café"], table.Header!);
            Assert.Equal(["1", "2"], table.Rows[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_empty_file_has_an_empty_header_and_no_rows()
    {
        var table = DelimitedReader.Parse(string.Empty);

        Assert.Empty(table.Header!);
        Assert.Empty(table.Rows);
        Assert.Equal(0, table.ColumnCount);
    }

    [Fact]
    public void The_quote_character_cannot_be_the_delimiter()
    {
        Assert.Throws<ArgumentException>(() => DelimitedReader.Parse("a", new DelimitedReadOptions { Delimiter = '"' }));
    }
}
