using FluentAssertions;
using RentalCommand.Api.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class LoanExtractionSchemaTests
{
    [Fact]
    public void LoanStatementSchema_UsesV2AndIncludesExactStatementComponents()
    {
        LoanExtractionSchema.PromptId.Should().Be("mortgage-statement-to-loan-v2");
        LoanExtractionSchema.Instructions.Should().Contain("OPENING unpaid principal balance");
        LoanExtractionSchema.Instructions.Should().Contain("statement_principal_amount");
        LoanExtractionSchema.Instructions.Should().Contain("statement_effective_date");

        var fieldNames = LoanExtractionSchema.Fields.Select(field => field.Name).ToArray();
        fieldNames.Should().Contain(new[]
        {
            "statement_principal_amount",
            "statement_interest_amount",
            "statement_escrow_amount",
            "statement_total_amount",
            "statement_effective_date",
        });
    }
}
