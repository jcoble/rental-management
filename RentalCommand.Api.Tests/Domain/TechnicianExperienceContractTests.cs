using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Load-bearing contracts for the assignment-only technician experience. The query tests use the
/// Npgsql translator without connecting to a database so client filtering cannot satisfy them.
/// </summary>
public sealed class TechnicianExperienceContractTests
{
    private static readonly WorkspaceReadScope Scope = new(
        17,
        23,
        Guid.Parse("d9d88978-4f4c-4539-b284-817c550f51db"),
        31,
        7);

    [Fact]
    public void AssignmentPage_IsOnePagedPostgreSqlStatementBeginningAtExactResponsibilityScope()
    {
        using var db = NewContext();
        var query = new TechnicianAssignmentQuery
        {
            Search = "Maple",
            OpenOnly = true,
            ScheduledFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            ScheduledTo = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            Sort = "scheduledFor",
            Skip = 20,
            Take = 20,
        };

        var sql = NewService(db).BuildAssignmentPageQuery(
            Scope, query, conversationsOnly: true, new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc))
            .ToQueryString();

        sql.Should().Contain("WorkOrderResponsibilities");
        sql.Should().Contain("public.rc_api_effective_capability_scopes(");
        sql.Should().Contain("public.rc_api_assigned_work_order_detail_context(");
        sql.Should().Contain(Scope.SessionId.ToString());
        sql.Should().NotContain("MembershipRoleAssignments");
        sql.Should().NotContain("WorkspaceMemberships");
        sql.Should().NotContain("WorkspaceAccessContexts");
        sql.Should().NotContain("RoleProfileCapabilities");
        sql.Should().NotContain("CapabilityDefinitions");
        sql.Should().NotContain("AuthSessions");
        sql.Should().Contain(CapabilityKeys.AssignedWorkRead);
        sql.Should().Contain("AssignedWorkOrders");
        sql.Should().Contain("Conversations");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.TrimStart().Should().StartWith("--", "Npgsql may emit parameter comments before one SELECT");
        sql.Count(character => character == ';').Should().BeLessThanOrEqualTo(1,
            "one assignment page must translate to one SQL command");
    }

    [Fact]
    public void AssignmentDetailDto_IncludesScanRoutingContext_ButOmitsManagementFinancialAndVendorFields()
    {
        var names = typeof(TechnicianAssignmentDetail).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().Contain([
            "PropertyId", "UnitId", "Address", "Unit", "AccessInstructions", "Timeline", "Entries", "Messages",
        ], "property and unit identity are contextual scan-routing metadata, not management authority");
        names.Should().NotContain([
            "PortfolioId", "TenantId", "LeaseId", "LeaseManagementId",
            "VendorId", "VendorName", "OwnerId", "EstimatedCost", "ActualCost", "Expenses",
            "ScanFileId", "ScanText", "HasScan",
        ]);
    }

    [Fact]
    public void TechnicianController_IsDedicatedAndDoesNotExposeManagementCreateDeleteOrDispatchCommands()
    {
        typeof(TechnicianController).Should().NotBeAssignableTo<ManagementControllerBase>();
        typeof(WorkOrderController).Should().BeAssignableTo<ManagementControllerBase>();

        var actions = typeof(TechnicianController).GetMethods()
            .Where(method => method.DeclaringType == typeof(TechnicianController))
            .ToArray();
        actions.Select(method => method.Name).Should().BeEquivalentTo(
            "List", "Schedule", "Inbox", "Detail", "UpdateAssignment", "RecordEntry", "SendMessage",
            "MarkConversationRead");
        actions.SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType)
            .Should().NotContain([typeof(CreateWorkOrderRequest), typeof(UpdateWorkOrderRequest)]);
        actions.SelectMany(method => method.GetCustomAttributes(typeof(HttpDeleteAttribute), inherit: true))
            .Should().BeEmpty();
    }

    [Fact]
    public void TechnicianUpdate_UsesCanonicalAssignmentRouteAndTransactionalAssignedWorkCommand()
    {
        var action = typeof(TechnicianController).GetMethod(nameof(TechnicianController.UpdateAssignment));
        action.Should().NotBeNull();
        action!.GetCustomAttributes(typeof(HttpPatchAttribute), inherit: true)
            .Cast<HttpPatchAttribute>()
            .Should().ContainSingle(attribute =>
                attribute.Template == "assignments/{workOrderId:int}");

        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "RentalCommand.Api",
            "Controllers", "TechnicianController.cs"));
        source.Should().Contain("new UpdateAssignedWorkOrderCommand(");
        source.Should().Contain("_writes.ExecuteAsync(");
        source.Should().Contain("UpdateAssignedWorkOrderRule.Write(command, _db)");
        source.Should().Contain("UpdateAssignedWorkOrderOutcome.Stale");
        source.Should().NotContain("assigned-update");
    }

    [Fact]
    public void FieldEntryRequest_AllowsOnlyNoteTimeMaterialAndAttachedPhotoShapes()
    {
        Enum.GetNames<TechnicianWorkEntryKind>()
            .Should().BeEquivalentTo("Note", "Time", "Material", "Photo");

        var properties = typeof(RecordTechnicianWorkEntryRequest).GetProperties()
            .Select(property => property.Name);
        properties.Should().BeEquivalentTo(
            "Kind", "Note", "Quantity", "Unit", "PhotoFileId", "OccurredAt");
        properties.Should().NotContain(name =>
            name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Rate", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Vendor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mutations_ReauthorizeExactAssignmentForStatusEntriesPhotosAndConversation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var entrySource = File.ReadAllText(Path.Combine(repositoryRoot, "RentalCommand.Data",
            "Operations", "TechnicianExperienceRules.cs"));
        var statusSource = File.ReadAllText(Path.Combine(repositoryRoot, "RentalCommand.Data",
            "Operations", "WorkOrderResponsibilityMutationRules.cs"));

        entrySource.Should().Contain("AuthorizeAsync(");
        entrySource.Should().Contain("CapabilityKeys.AssignedWorkTimeMaterialsManage");
        entrySource.Should().Contain("CapabilityKeys.AssignedWorkConverse");
        entrySource.Should().Contain("CapabilityAuthorizationTargetKind.WorkOrder");
        entrySource.Should().Contain("MembershipRoleAssignmentScopeKind.AssignedWorkOrders");
        entrySource.Should().Contain("file.ContentType.StartsWith(\"image/\")");
        entrySource.Should().Contain("file.EntityId == command.WorkOrderId");
        entrySource.Should().NotContain("EstimatedCost");
        entrySource.Should().NotContain("ActualCost");
        entrySource.Should().NotContain("Expense");
        entrySource.Should().NotContain("Vendor");

        statusSource.Should().Contain("AuthorizeAsync(");
        statusSource.Should().Contain("CapabilityKeys.AssignedWorkUpdate");
        statusSource.Should().Contain(
            "AuthorizeAndLoadAsync(command, _db, securityNowUtc, businessNowUtc, tracking: false, ct)");
        statusSource.Should().Contain("WorkOrderStatus.InProgress");
        statusSource.Should().Contain("WorkOrderStatus.Completed");
    }

    private static TechnicianExperienceService NewService(RentalCommandDbContext db) =>
        new(db, TimeProvider.System);

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
