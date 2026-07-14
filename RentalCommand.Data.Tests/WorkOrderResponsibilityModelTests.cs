using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Tests;

public sealed class WorkOrderResponsibilityModelTests
{
    private static RentalCommandDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RentalCommandDbContext(options);
    }

    [Fact]
    public void Responsibility_UsesExactAuthorityAndWorkOrderCompositeForeignKeys()
    {
        using var db = CreateDb();
        var entity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(WorkOrderResponsibility))!;

        entity.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(WorkOrderResponsibility.Id));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(WorkOrderResponsibility.WorkOrderId),
                nameof(WorkOrderResponsibility.PropertyId),
                nameof(WorkOrderResponsibility.PortfolioId),
            }));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(WorkOrderResponsibility.MembershipRoleAssignmentId),
                nameof(WorkOrderResponsibility.WorkspaceMembershipId),
                nameof(WorkOrderResponsibility.PortfolioId),
            }));
    }

    [Fact]
    public void Responsibility_CurrentPrimaryAndMemberIndexesAreUniqueAndFiltered()
    {
        using var db = CreateDb();
        var entity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(WorkOrderResponsibility))!;

        entity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.GetDatabaseName() == "UX_WorkOrderResponsibilities_CurrentPrimary" &&
            index.GetFilter()!.Contains("Kind"));
        entity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.GetDatabaseName() == "UX_WorkOrderResponsibilities_CurrentMember" &&
            index.GetFilter() == "\"EffectiveToUtc\" IS NULL");
    }

    [Fact]
    public void CleanBaseline_ProtectsPeriodsHistoryAndTenantIsolation()
    {
        var createSql = string.Join(Environment.NewLine, FoundationBaselinePostgreSql.CreateStatements);

        FoundationBaselinePostgreSql.DirectPortfolioTables
            .Should().Contain("WorkOrderResponsibilities");
        createSql.Should().Contain("EX_WorkOrderResponsibilities_NoMemberOverlap");
        createSql.Should().Contain("EX_WorkOrderResponsibilities_NoPrimaryOverlap");
        createSql.Should().Contain("TR_WorkOrderResponsibilities_AppendPreserved");
        createSql.Should().Contain("ALTER TABLE \"WorkOrderResponsibilities\" ENABLE ROW LEVEL SECURITY");
    }
}
