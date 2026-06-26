using FluentAssertions;
using RentalCommand.Api.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class ImageOcrRoutingTests
{
    [Fact]
    public void BuildHybridHint_MakesImageAuthoritativeAndExplainsFlattenedTables()
    {
        var hint = ImageOcrRouting.BuildHybridHint(
            "Description\nRepair labor\nQty\n1.5\nUnit Price\n$95.00\nAmount\n$142.50");

        hint.Should().Contain("image is authoritative");
        hint.Should().Contain("OCR text from local image extraction");
        hint.Should().Contain("flattens a receipt/invoice table");
        hint.Should().Contain("reconstruct rows");
        hint.Should().Contain("without inventing absent cells");
    }
}
