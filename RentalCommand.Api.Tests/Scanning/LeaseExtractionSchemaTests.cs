using FluentAssertions;
using RentalCommand.Api.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class LeaseExtractionSchemaTests
{
    [Fact]
    public void Fields_IncludeTenantContactFields()
    {
        var fieldNames = LeaseExtractionSchema.Fields.Select(f => f.Name).ToArray();

        fieldNames.Should().ContainInOrder(
            "tenant_name",
            "tenant_email",
            "tenant_phone",
            "tenant_emergency_contact");

        LeaseExtractionSchema.Instructions.Should().Contain("tenant_email");
        LeaseExtractionSchema.Instructions.Should().Contain("tenant_phone");
    }

    [Fact]
    public void Instructions_CallOutBedBathShorthandAndNoZeroDefaults()
    {
        LeaseExtractionSchema.Instructions.Should().Contain("2BR/1BA");
        LeaseExtractionSchema.Instructions.Should().Contain("half bath");
        LeaseExtractionSchema.Instructions.Should().Contain("Do not default missing bed/bath counts to 0");

        LeaseExtractionSchema.Fields.Single(f => f.Name == "unit_bedrooms")
            .Description.Should().Contain("Never return 0");
        LeaseExtractionSchema.Fields.Single(f => f.Name == "unit_bathrooms")
            .Description.Should().Contain("1/2 bath");
    }
}
