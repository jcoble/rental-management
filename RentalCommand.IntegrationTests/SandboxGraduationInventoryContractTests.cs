using FluentAssertions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Data;

namespace RentalCommand.IntegrationTests;

public sealed class SandboxGraduationInventoryContractTests
{
    [Fact]
    public void ExecutableDeleteOrder_MatchesThePostgreSqlAdmissionInventory()
    {
        SandboxService.SandboxGraduationDeleteOrder.Should().OnlyHaveUniqueItems();
        SandboxService.SandboxGraduationDeleteOrder.Should().BeEquivalentTo(
            FoundationBaselinePostgreSql.SandboxGraduationDeleteTables);
    }
}
