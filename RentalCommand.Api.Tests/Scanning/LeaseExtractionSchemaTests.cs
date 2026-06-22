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
}
