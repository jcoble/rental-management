using FluentAssertions;
using RentalCommand.Api.Services.Import;

namespace RentalCommand.Api.Tests.Import;

public class CsvParserTests
{
    [Fact]
    public void Parse_TrimsHeader_AndSplitsSimpleRows()
    {
        var table = CsvParser.Parse(" a , b , c \n1,2,3\n4,5,6\n");

        table.Header.Should().Equal("a", "b", "c");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("1", "2", "3");
        table.Rows[1].Should().Equal("4", "5", "6");
    }

    [Fact]
    public void Parse_HandlesQuotedFields_WithCommas_Quotes_AndNewlines()
    {
        // Field 1: quoted comma; field 2: escaped quote (""); field 3: embedded newline.
        const string csv =
            "name,note,address\n" +
            "\"Doe, Jane\",\"She said \"\"hi\"\"\",\"12 Main St\nApt 4\"\n";

        var table = CsvParser.Parse(csv);

        table.Header.Should().Equal("name", "note", "address");
        table.Rows.Should().HaveCount(1);
        table.Rows[0][0].Should().Be("Doe, Jane");
        table.Rows[0][1].Should().Be("She said \"hi\"");
        table.Rows[0][2].Should().Be("12 Main St\nApt 4");
    }

    [Fact]
    public void Parse_HandlesCrlf_AndSkipsBlankLines()
    {
        const string csv = "a,b\r\n1,2\r\n\r\n3,4\r\n";

        var table = CsvParser.Parse(csv);

        table.Header.Should().Equal("a", "b");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("1", "2");
        table.Rows[1].Should().Equal("3", "4");
    }

    [Fact]
    public void Parse_PadsShortRows_ToReadMissingColumnsSafely()
    {
        var table = CsvParser.Parse("a,b,c\n1\n");

        table.Rows[0].Should().Equal("1"); // shorter row is kept; callers tolerate missing cells
        table.Rows[0].Length.Should().Be(1);
    }

    [Fact]
    public void Parse_NoTrailingNewline_StillReadsLastRow()
    {
        var table = CsvParser.Parse("a,b\n1,2");

        table.Rows.Should().HaveCount(1);
        table.Rows[0].Should().Equal("1", "2");
    }

    [Fact]
    public void Parse_EmptyInput_Throws()
    {
        var act = () => CsvParser.Parse("");
        act.Should().Throw<CsvFormatException>();
    }
}
