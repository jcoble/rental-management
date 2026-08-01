using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Operations;

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
        using var db = CreateDb();
        var entity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(WorkOrderResponsibility))!;
        var createSql = string.Join(Environment.NewLine, FoundationBaselinePostgreSql.CreateStatements);
        var dropSql = string.Join(Environment.NewLine, FoundationBaselinePostgreSql.DropStatements);

        FoundationBaselinePostgreSql.DirectPortfolioTables
            .Should().Contain("WorkOrderResponsibilities");
        entity.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_WorkOrderResponsibilities_EffectivePeriod" &&
            constraint.Sql.Contains("\"EffectiveToUtc\" > \"EffectiveFromUtc\""));
        entity.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_WorkOrderResponsibilities_Kind" &&
            constraint.Sql.Contains("\"Kind\" IN ('Primary', 'Supporting')"));
        entity.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_WorkOrderResponsibilities_AssignedFacts" &&
            constraint.Sql.Contains("\"AssignedAtUtc\" = \"EffectiveFromUtc\"") &&
            constraint.Sql.Contains("length(btrim(\"AssignedReason\")) > 0"));
        entity.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_WorkOrderResponsibilities_EndFacts" &&
            constraint.Sql.Contains("length(btrim(\"EndedReason\")) > 0"));
        createSql.Should().Contain("EX_WorkOrderResponsibilities_NoMemberOverlap");
        createSql.Should().Contain("EX_WorkOrderResponsibilities_NoPrimaryOverlap");
        createSql.Should().Contain("TR_WorkOrderResponsibilities_AppendPreserved");
        createSql.Should().Contain("ALTER TABLE \"WorkOrderResponsibilities\" ENABLE ROW LEVEL SECURITY");
        createSql.Should().NotContain("CREATE TABLE \"WorkOrderResponsibilities\"");
        createSql.Should().NotContain("CK_WorkOrderResponsibilities_Kind");
        dropSql.Should().NotContain("DROP TABLE IF EXISTS \"WorkOrderResponsibilities\"");
    }

    [Fact]
    public void AssignedUpdate_ExposesRequiredConcurrencyAndTypedStaleContract()
    {
        typeof(UpdateAssignedWorkOrderCommand).GetProperty(nameof(UpdateAssignedWorkOrderCommand.ExpectedUpdatedAtUtc))
            .Should().NotBeNull();
        Enum.GetNames<UpdateAssignedWorkOrderOutcome>()
            .Should().BeEquivalentTo(nameof(UpdateAssignedWorkOrderOutcome.Applied),
                nameof(UpdateAssignedWorkOrderOutcome.Stale));
        typeof(UpdateAssignedWorkOrderResult).GetProperty(nameof(UpdateAssignedWorkOrderResult.Outcome))
            .Should().NotBeNull();
    }

    [Fact]
    public void ResponsibilityCommands_UseAtomicDataOnlyRevisionExpectations()
    {
        typeof(WorkspaceAccessRevisionExpectation)
            .Should().BeAssignableTo<IAtomicCommandData>();
        typeof(AssignWorkOrderResponsibilityCommand)
            .Should().BeAssignableTo<IAtomicCommandData>();
        typeof(CloseWorkOrderResponsibilityCommand)
            .Should().BeAssignableTo<IAtomicCommandData>();
    }

    [Fact]
    public void AssignedUpdate_HandlerFencesStaleWritesAndReauthorizesReplay()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "RentalCommand.Data",
            "Operations", "WorkOrderResponsibilityMutationHandlers.cs"));

        source.Should().Contain("workOrder.UpdatedAt != command.ExpectedUpdatedAtUtc");
        source.Should().Contain("UpdateAssignedWorkOrderOutcome.Stale");
        source.Should().Contain("AuthorizeReplayAsync(");
        source.Should().Contain(
            "AuthorizeAndLoadAsync(command, _db, securityNowUtc, businessNowUtc, tracking: false");
        source.Should().Contain("resultingStatus != WorkOrderStatus.Completed");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
