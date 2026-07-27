using FluentAssertions;
using RentalCommand.Api.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class ReceiptExtractionSchemaTests
{
    [Fact]
    public void Instructions_CallOutPrintedChargeRowsAndExcludedSummaryRows()
    {
        ReceiptExtractionSchema.Instructions.Should().Contain("printed charge row");
        ReceiptExtractionSchema.Instructions.Should().Contain("preserving the printed order");
        ReceiptExtractionSchema.Instructions.Should().Contain("OCR text may flatten tables");
        ReceiptExtractionSchema.Instructions.Should().Contain("matching values in printed column order");
        ReceiptExtractionSchema.Instructions.Should().Contain("Do not include subtotal, tax, tip, discount, shipping, payment, balance, or total rows");
        ReceiptExtractionSchema.Instructions.Should().Contain("Do not reorder, reverse, or infer date digits");
        ReceiptExtractionSchema.Instructions.Should().Contain("document header date may supply transaction_date");

        var lineItems = ReceiptExtractionSchema.Fields.Single(f => f.Name == "line_items");
        lineItems.Description.Should().Contain("excluding subtotal/tax/tip/discount/shipping/payment/total rows");
        lineItems.ItemFields.Should().NotBeNull();
        lineItems.ItemFields!.Single(f => f.Name == "amount")
            .Description.Should().Contain("only when visible");

        var transactionDate = ReceiptExtractionSchema.Fields.Single(f => f.Name == "transaction_date");
        transactionDate.Description.Should().Contain("Copy a complete visible date exactly");
        transactionDate.Description.Should().Contain("Empty when no complete date is visible");
    }
}
