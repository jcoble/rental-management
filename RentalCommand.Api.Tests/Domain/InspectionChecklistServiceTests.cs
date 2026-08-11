using FluentAssertions;
using FluentAssertions.Execution;
using System.Text;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the smart-checklist inspection workflow: built-in templates surface from the templates
/// endpoint, starting an inspection from a template materializes Pending items, and completing an
/// inspection spawns a work order per Fail item, flips status to Completed, and records a report file.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class InspectionChecklistServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _executedSql = [];
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private InspectionService _service = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;
    private int _operationSequence;

    public InspectionChecklistServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        // The real QuestPDF generator runs in the completion test; license must be set once.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_executedSql)]);
        _db = _ctx.Db;
        _services = AtomicDomainTestKernel.CreateForInspectionsPostgreSql(_ctx.ConnectionString);
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(InspectionChecklistServiceTests));

        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            TimeProvider.System,
            _services.GetRequiredService<IAtomicUnitOfWork>());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private string NextOperationKey() => $"inspection-checklist-{++_operationSequence}";

    [Fact]
    public async Task ListTemplates_ReturnsBuiltIns_WithItems()
    {
        var templates = await _service.ListTemplatesAsync(PortfolioId);

        templates.Should().HaveCountGreaterThanOrEqualTo(3);
        templates.Where(t => t.IsBuiltIn).Should().HaveCount(3);
        templates.Select(t => t.InspectionType).Should().Contain(new[]
        {
            InspectionType.MoveIn, InspectionType.MoveOut, InspectionType.AnnualSafety
        });
        // Built-in ids are negative so they never collide with DB-generated custom ids.
        templates.Where(t => t.IsBuiltIn).Should().OnlyContain(t => t.Id < 0 && t.PortfolioId == null);
        templates.First(t => t.InspectionType == InspectionType.AnnualSafety).Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListPageAsync_ReturnsDbCountAndFinalFullPage()
    {
        var property = SeedProperty();
        var day1 = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        SeedInspection(property, day1, "First");
        SeedInspection(property, day1.AddDays(1), "Second");
        SeedInspection(property, day1.AddDays(2), "Third");
        SeedInspection(property, day1.AddDays(3), "Fourth");

        _executedSql.Clear();
        var page = await _service.ListPageAsync(PortfolioId, property.Id, new ListQuery
        {
            Skip = 2,
            Take = 2,
            Sort = "scheduledFor",
        });

        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(2);
        page.Take.Should().Be(2);
        page.Items.Select(i => i.Outcome).Should().Equal("Third", "Fourth");

        _executedSql.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Inspections", StringComparison.OrdinalIgnoreCase));
        _executedSql.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ScheduledFor", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_FromTemplate_MaterializesPendingItems()
    {
        var property = SeedProperty();
        var annualTemplate = InspectionTemplateCatalog.BuiltIns
            .First(t => t.InspectionType == InspectionType.AnnualSafety);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.AnnualSafety,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = annualTemplate.Id, // negative built-in id
        }, NextOperationKey());

        created.Should().NotBeNull();
        created!.TemplateId.Should().Be(annualTemplate.Id);
        created.Items.Should().HaveCount(annualTemplate.Items.Count);
        created.Items.Should().OnlyContain(i => i.Result == InspectionItemResult.Pending);
        created.Items.Select(i => i.SortOrder).Should().BeInAscendingOrder();

        // Items are persisted and reachable via GET detail.
        var detail = await _service.GetAsync(PortfolioId, created.Id);
        detail!.Items.Should().HaveCount(annualTemplate.Items.Count);
    }

    [Fact]
    public async Task ScheduledInspection_AllowsChecklistQuestionCustomization()
    {
        var property = SeedProperty();
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.Routine,
            ScheduledFor = DateTime.UtcNow,
        }, NextOperationKey());
        created.Should().NotBeNull();
        created!.Items.Should().BeEmpty();

        var first = await _service.CreateItemAuthorizedAsync(_scope, created.Id, new CreateInspectionItemRequest
        {
            Area = " Kitchen ",
            Label = " Sink drains ",
        }, NextOperationKey());
        var second = await _service.CreateItemAuthorizedAsync(_scope, created.Id, new CreateInspectionItemRequest
        {
            Area = "Safety",
            Label = "Smoke detector works",
        }, NextOperationKey());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Area.Should().Be("Kitchen");
        first.Label.Should().Be("Sink drains");

        var updated = await _service.UpdateItemAuthorizedAsync(_scope, created.Id, first.Id, new UpdateInspectionItemRequest
        {
            Area = "Kitchenette",
            Label = "Sink and faucet are dry",
            Result = InspectionItemResult.Pass,
            Note = "No drip.",
        }, NextOperationKey());

        updated.Should().NotBeNull();
        updated!.Area.Should().Be("Kitchenette");
        updated.Label.Should().Be("Sink and faucet are dry");
        updated.Result.Should().Be(InspectionItemResult.Pass);
        updated.Note.Should().Be("No drip.");

        var reordered = await _service.ReorderItemsAuthorizedAsync(_scope, created.Id, new ReorderInspectionItemsRequest
        {
            ItemIds = [second!.Id, first.Id],
        }, NextOperationKey());
        reordered.Should().NotBeNull();
        reordered!.Select(i => i.Id).Should().Equal(second.Id, first.Id);
        reordered.Select(i => i.SortOrder).Should().Equal(0, 1);

        (await _service.DeleteItemAuthorizedAsync(_scope, created.Id, second.Id, NextOperationKey())).Should().BeTrue();

        var detail = await _service.GetAsync(PortfolioId, created.Id);
        detail!.Items.Should().ContainSingle();
        detail.Items[0].Id.Should().Be(first.Id);
        detail.Items[0].Area.Should().Be("Kitchenette");
    }

    [Fact]
    public async Task ChecklistQuestionCustomization_ValidatesRequiredTextAndReorderMembership()
    {
        var property = SeedProperty();
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.Routine,
            ScheduledFor = DateTime.UtcNow,
        }, NextOperationKey());
        created.Should().NotBeNull();

        Func<Task> createWithoutArea = () => _service.CreateItemAuthorizedAsync(_scope, created!.Id, new CreateInspectionItemRequest
        {
            Area = " ",
            Label = "Window locks",
        }, NextOperationKey());
        (await createWithoutArea.Should().ThrowAsync<DomainValidationException>())
            .Which.Message.Should().Contain("area");

        var item = await _service.CreateItemAuthorizedAsync(_scope, created!.Id, new CreateInspectionItemRequest
        {
            Area = "Doors",
            Label = "Front door latches",
        }, NextOperationKey());

        Func<Task> updateWithoutLabel = () => _service.UpdateItemAuthorizedAsync(_scope, created.Id, item!.Id, new UpdateInspectionItemRequest
        {
            Label = "",
        }, NextOperationKey());
        (await updateWithoutLabel.Should().ThrowAsync<DomainValidationException>())
            .Which.Message.Should().Contain("Question 1 item");

        Func<Task> reorderMissingItem = () => _service.ReorderItemsAuthorizedAsync(_scope, created.Id, new ReorderInspectionItemsRequest
        {
            ItemIds = [999_999],
        }, NextOperationKey());
        (await reorderMissingItem.Should().ThrowAsync<DomainValidationException>())
            .Which.Message.Should().Contain("every checklist question exactly once");
    }

    [Fact]
    public async Task Create_WithUnknownTemplate_ReturnsNull()
    {
        var property = SeedProperty();

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = 99999, // not a built-in (positive) and not an in-portfolio custom template
        }, NextOperationKey());

        created.Should().BeNull("an unknown template id must be rejected");
    }

    [Fact]
    public async Task Complete_SpawnsWorkOrderPerFail_SetsCompleted_AndReport()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        // Mark two items Fail, one Pass — leave the rest Pending.
        var items = created!.Items.OrderBy(i => i.SortOrder).ToList();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Faucet leaks badly" }, NextOperationKey());
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[1].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Burner won't ignite" }, NextOperationKey());
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[2].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-spawns-work-orders");

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.Status.Should().Be(InspectionStatus.Completed);
        summary.FailCount.Should().Be(2);
        summary.PassCount.Should().Be(1);
        summary.CreatedWorkOrderIds.Should().HaveCount(2);
        summary.ReportStoredFileId.Should().NotBeNull();

        // Inspection row is Completed and carries the report file id.
        var inspection = await _db.Inspections.AsNoTracking().FirstAsync(i => i.Id == created.Id);
        inspection.Status.Should().Be(InspectionStatus.Completed);
        inspection.CompletedAt.Should().NotBeNull();
        inspection.ReportStoredFileId.Should().Be(summary.ReportStoredFileId);

        // A real work order exists for each Fail item, linked both ways.
        var failedItems = await _db.InspectionItems.AsNoTracking()
            .Where(i => i.InspectionId == created.Id && i.Result == InspectionItemResult.Fail)
            .ToListAsync();
        failedItems.Should().OnlyContain(i => i.SpawnedWorkOrderId != null);

        foreach (var woId in summary.CreatedWorkOrderIds)
        {
            var wo = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == woId);
            wo.PropertyId.Should().Be(property.Id);
            wo.Category.Should().Be("Inspection");
            // Initial status event written in the same save as the work order.
            (await _db.WorkOrderStatusEvents.CountAsync(e => e.WorkOrderId == woId))
                .Should().BeGreaterThanOrEqualTo(1);
        }

        // The generated report is a StoredFile linked to the inspection.
        var report = await _db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == summary.ReportStoredFileId);
        report.EntityType.Should().Be("Inspection");
        report.EntityId.Should().Be(created.Id);
        report.ContentType.Should().Be("application/pdf");
        report.FileSize.Should().BeGreaterThan(0);

        // The report is downloadable.
        var download = await _service.GetReportAsync(PortfolioId, created.Id);
        download.Should().NotBeNull();
        download!.Value.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task ReopenThenRecomplete_ClearsCompletionArtifactsAndGeneratesFreshReport()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var items = created!.Items.OrderBy(i => i.SortOrder).ToList();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Before reopen" }, NextOperationKey());
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[1].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());
        var (firstSummary, firstError) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: NextOperationKey());
        firstError.Should().BeNull();
        firstSummary.Should().NotBeNull();
        var oldReportId = firstSummary!.ReportStoredFileId;
        oldReportId.Should().NotBeNull();
        var firstWorkOrderIds = firstSummary.CreatedWorkOrderIds.ToArray();
        firstWorkOrderIds.Should().ContainSingle();
        var oldCompletedAt = await _db.Inspections.AsNoTracking()
            .Where(inspection => inspection.Id == created.Id)
            .Select(inspection => inspection.CompletedAt)
            .SingleAsync();

        var reopened = await _service.UpdateAuthorizedAsync(_scope, created.Id,
            new UpdateInspectionRequest { Status = InspectionStatus.Scheduled }, NextOperationKey());
        reopened.Should().NotBeNull();

        using (new AssertionScope())
        {
            var reopenedRow = await _db.Inspections.AsNoTracking().SingleAsync(inspection => inspection.Id == created.Id);
            reopenedRow.Status.Should().Be(InspectionStatus.Scheduled);
            reopenedRow.CompletedAt.Should().BeNull();
            reopenedRow.ReportStoredFileId.Should().BeNull();
            (await _db.StoredFiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(file => file.Id == oldReportId!.Value))
                .DeletedAt.Should().NotBeNull();
            (await _db.InspectionItems.AsNoTracking()
                .Where(item => item.InspectionId == created.Id)
                .Select(item => item.SpawnedWorkOrderId)
                .ToListAsync())
                .Should().OnlyContain(workOrderId => workOrderId == null);
            var retiredWorkOrder = await _db.WorkOrders.AsNoTracking()
                .SingleAsync(workOrder => workOrder.Id == firstWorkOrderIds[0]);
            retiredWorkOrder.Status.Should().Be(WorkOrderStatus.Cancelled);
            retiredWorkOrder.DeletedAt.Should().BeNull("cancellation keeps the work-order history row");
            (await _db.WorkOrderStatusEvents.AsNoTracking()
                .Where(statusEvent => statusEvent.WorkOrderId == retiredWorkOrder.Id)
                .OrderBy(statusEvent => statusEvent.Id)
                .Select(statusEvent => new { statusEvent.FromStatus, statusEvent.ToStatus, statusEvent.Visibility })
                .ToListAsync())
                .Should().Contain(eventRow =>
                    eventRow.FromStatus == WorkOrderStatus.New
                    && eventRow.ToStatus == WorkOrderStatus.Cancelled
                    && eventRow.Visibility == "Public");
            (await _db.WorkOrders.AsNoTracking()
                .Where(workOrder => workOrder.Id == retiredWorkOrder.Id
                    && workOrder.Status != WorkOrderStatus.Completed
                    && workOrder.Status != WorkOrderStatus.Cancelled
                    && workOrder.Status != WorkOrderStatus.Archived)
                .CountAsync()).Should().Be(0);

            await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
                new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "After reopen" }, NextOperationKey());
            var (secondSummary, secondError) = await _service.CompleteAuthorizedAsync(
                _scope, created.Id, userId: 7, operationKey: NextOperationKey());
            secondError.Should().BeNull();
            secondSummary.Should().NotBeNull();
            secondSummary!.ReportStoredFileId.Should().NotBeNull();
            secondSummary.ReportStoredFileId.Should().NotBe(oldReportId);
            secondSummary.CreatedWorkOrderIds.Should().ContainSingle();
            secondSummary.CreatedWorkOrderIds.Should().NotContain(firstWorkOrderIds);
            (await (from item in _db.InspectionItems.AsNoTracking()
                    join workOrder in _db.WorkOrders.AsNoTracking()
                        on item.SpawnedWorkOrderId equals workOrder.Id
                    where item.Id == items[0].Id
                        && workOrder.DeletedAt == null
                        && workOrder.Status != WorkOrderStatus.Completed
                        && workOrder.Status != WorkOrderStatus.Cancelled
                        && workOrder.Status != WorkOrderStatus.Archived
                    select workOrder.Id).CountAsync()).Should().Be(1);

            var recompleted = await _db.Inspections.AsNoTracking().SingleAsync(inspection => inspection.Id == created.Id);
            recompleted.CompletedAt.Should().NotBe(oldCompletedAt);
            recompleted.ReportStoredFileId.Should().Be(secondSummary.ReportStoredFileId);
        }
    }

    [Fact]
    public async Task Complete_UsesBusinessClockForCompletionWorkOrdersAndReport()
    {
        var businessNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(businessNowUtc.AddMinutes(-2)));
        var logger = new CapturingLogger<InspectionService>();
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            logger,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = businessNowUtc,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var item = created!.Items.OrderBy(i => i.SortOrder).First();
        clock.SetUtcNow(new DateTimeOffset(businessNowUtc.AddMinutes(-1)));
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Latch sticks" }, NextOperationKey());

        clock.SetUtcNow(new DateTimeOffset(businessNowUtc));
        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-business-clock");

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.ReportStoredFileId.Should().NotBeNull(logger.LastError?.ToString());
        var inspection = await _db.Inspections.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        inspection.CompletedAt.Should().Be(businessNowUtc);
        inspection.UpdatedAt.Should().Be(businessNowUtc);
        var workOrderId = summary.CreatedWorkOrderIds.Should().ContainSingle().Subject;
        var workOrder = await _db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrderId);
        workOrder.RequestedAt.Should().Be(businessNowUtc);
        workOrder.UpdatedAt.Should().Be(businessNowUtc);
        var statusEvent = await _db.WorkOrderStatusEvents.AsNoTracking()
            .SingleAsync(e => e.WorkOrderId == workOrderId);
        statusEvent.CreatedAtUtc.Should().Be(businessNowUtc);
        var report = await _db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == summary.ReportStoredFileId);
        report.UploadedAt.Should().Be(businessNowUtc);
    }

    [Fact]
    public async Task Complete_UsesDatabaseWallClockForPendingUpload_WhenBusinessTimeIsPastCleanupTtl()
    {
        var businessNowUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-2), DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(businessNowUtc));
        var storage = new CleanupRaceFileStorage();
        storage.BeforeUploadAsync = async _ =>
        {
            await using var cleanupScope = _services.CreateAsyncScope();
            var claims = await cleanupScope.ServiceProvider
                .GetRequiredService<IPendingFileUploadStore>()
                .ClaimExpiredAsync(
                    "inspection-business-time-cleanup",
                    TimeSpan.FromHours(24),
                    TimeSpan.FromMinutes(5),
                    10);
            storage.Claims.AddRange(claims);
        };
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            storage,
            new DeterministicInspectionPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());

        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = businessNowUtc,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var items = created!.Items.OrderBy(item => item.SortOrder).Take(2).ToArray();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail }, NextOperationKey());
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[1].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "inspection-business-time-cleanup-fence");

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.ReportStoredFileId.Should().NotBeNull();
        storage.Claims.Should().BeEmpty("a newly admitted upload must not be cleanup-eligible because business time is old");
        storage.UploadCount.Should().Be(1);

        var pending = await _db.PendingFileUploads.AsNoTracking()
            .SingleAsync(upload => upload.Purpose == "inspection-report-pdf"
                && upload.ActorScopeId == _scope.UserId);
        pending.State.Should().Be(PendingFileUploadState.Finalized);
        pending.CreatedAtUtc.Should().BeAfter(DateTime.UtcNow.AddHours(-1));
        var report = await _db.StoredFiles.AsNoTracking().SingleAsync(file => file.Id == summary.ReportStoredFileId);
        report.UploadedAt.Should().Be(businessNowUtc);
    }

    [Fact]
    public async Task Complete_RetryAfterCleanupReturnsConflictWithoutReportSideEffects()
    {
        var storage = new CleanupRaceFileStorage();
        storage.BeforeUploadAsync = async _ =>
        {
            await using var cleanupScope = _services.CreateAsyncScope();
            var cleanupStore = cleanupScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>();
            var claims = await cleanupStore.ClaimExpiredAsync(
                "inspection-abandoned-cleanup",
                TimeSpan.Zero,
                TimeSpan.FromMinutes(5),
                10);
            storage.Claims.AddRange(claims);
            foreach (var claim in claims)
                await cleanupStore.MarkAbandonedAsync(claim.Id, claim.ClaimOwner, claim.ClaimToken);
        };
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            storage,
            new DeterministicInspectionPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            TimeProvider.System,
            _services.GetRequiredService<IAtomicUnitOfWork>());

        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var items = created!.Items.OrderBy(item => item.SortOrder).Take(2).ToArray();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail }, NextOperationKey());
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[1].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());
        const string operationKey = "inspection-abandoned-retry";
        var reportDataUpdatesBefore = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "data-update"
                && message.IdempotencyKey.EndsWith(":report-file"));

        var firstError = await Record.ExceptionAsync(() => _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey));
        var pendingAfterFirst = await _db.PendingFileUploads.AsNoTracking()
            .SingleAsync(upload => upload.Purpose == "inspection-report-pdf");
        await using var retryScope = _services.CreateAsyncScope();
        var retryDb = retryScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var retryService = new InspectionService(
            retryDb,
            new NoopInspectionDataUpdate(),
            storage,
            new DeterministicInspectionPdfGenerator(),
            retryScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            TimeProvider.System,
            retryScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>());
        var secondError = await Record.ExceptionAsync(() => retryService.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey));

        firstError.Should().BeOfType<DomainValidationException>();
        secondError.Should().BeOfType<DomainValidationException>(
            $"pending state after first attempt was {pendingAfterFirst.State} (claim {pendingAfterFirst.CleanupClaimToken})");
        firstError!.Message.Should().Contain("retry with a new request key");
        secondError!.Message.Should().Contain("retry with a new request key");
        storage.UploadCount.Should().Be(1);
        (await _db.StoredFiles.AsNoTracking()
            .CountAsync(file => file.EntityType == nameof(Inspection) && file.EntityId == created.Id))
            .Should().Be(0);
        (await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "data-update"
                && message.IdempotencyKey.EndsWith(":report-file")))
            .Should().Be(reportDataUpdatesBefore);
        (await _db.PendingFileUploads.AsNoTracking()
            .SingleAsync(upload => upload.Purpose == "inspection-report-pdf"))
            .State.Should().Be(PendingFileUploadState.Abandoned);
    }

    [Fact]
    public async Task Complete_UsesCurrentSecurityAccessWhenBusinessClockPredatesAccess()
    {
        var securityNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var businessNowUtc = securityNowUtc.AddYears(-1);
        var clock = new MutableTimeProvider(new DateTimeOffset(businessNowUtc));
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = businessNowUtc,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var item = created!.Items.OrderBy(candidate => candidate.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Latch sticks" }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-business-before-access");

        error.Should().BeNull();
        summary.Should().NotBeNull();
        var inspection = await _db.Inspections.AsNoTracking().SingleAsync(row => row.Id == created.Id);
        inspection.CompletedAt.Should().Be(businessNowUtc);
        inspection.UpdatedAt.Should().Be(businessNowUtc);
    }

    [Fact]
    public async Task RecoverChronologyAuthorizedAsync_CorrectsCompletedAtRetiresReportAndReplaysExactly()
    {
        var originalBusinessNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var correctedCompletedAtUtc = DateTime.SpecifyKind(
            originalBusinessNowUtc.AddDays(-2),
            DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(originalBusinessNowUtc));
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = originalBusinessNowUtc,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var firstItem = created!.Items.OrderBy(item => item.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, firstItem.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Recovery setup mutation" }, NextOperationKey());
        var (completed, completeError) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: NextOperationKey());
        completeError.Should().BeNull();
        completed.Should().NotBeNull();
        completed!.ReportStoredFileId.Should().NotBeNull();
        var contaminatedReportId = completed.ReportStoredFileId.Value;
        var contaminatedReportUploadedAtUtc = originalBusinessNowUtc.AddMinutes(3);
        await _db.Inspections
            .Where(inspection => inspection.Id == created.Id && inspection.PortfolioId == PortfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(inspection => inspection.CompletedAt, (DateTime?)originalBusinessNowUtc)
                .SetProperty(inspection => inspection.UpdatedAt, originalBusinessNowUtc));
        await _db.StoredFiles
            .Where(file => file.Id == contaminatedReportId && file.PortfolioId == PortfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(file => file.UploadedAt, contaminatedReportUploadedAtUtc));

        clock.SetUtcNow(new DateTimeOffset(correctedCompletedAtUtc));
        var recoveryKey = "inspection-chronology-recovery-ys-263";
        var request = new RecoverInspectionChronologyRequest
        {
            ExpectedContaminatedCompletedAtUtc = originalBusinessNowUtc,
            ExpectedContaminatedReportStoredFileId = contaminatedReportId,
            ExpectedContaminatedReportUploadedAtUtc = contaminatedReportUploadedAtUtc,
            CorrectCompletedAtUtc = correctedCompletedAtUtc,
        };

        var (recovered, recoveryError) = await _service.RecoverChronologyAuthorizedAsync(
            _scope, created.Id, request, recoveryKey);

        recoveryError.Should().BeNull();
        recovered.Should().NotBeNull();
        recovered!.CompletedAt.Should().Be(correctedCompletedAtUtc);
        recovered.RetiredReportStoredFileId.Should().Be(contaminatedReportId);
        recovered.ReportStoredFileId.Should().NotBeNull();
        recovered.ReportStoredFileId.Should().NotBe(contaminatedReportId);
        var inspection = await _db.Inspections.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        inspection.CompletedAt.Should().Be(correctedCompletedAtUtc);
        inspection.UpdatedAt.Should().Be(correctedCompletedAtUtc);
        inspection.ReportStoredFileId.Should().Be(recovered.ReportStoredFileId);
        var retiredReport = await _db.StoredFiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(file => file.Id == contaminatedReportId && file.PortfolioId == PortfolioId);
        retiredReport.DeletedAt.Should().Be(correctedCompletedAtUtc);
        var replacementReport = await _db.StoredFiles.AsNoTracking()
            .SingleAsync(file => file.Id == recovered.ReportStoredFileId);
        replacementReport.UploadedAt.Should().Be(correctedCompletedAtUtc);
        replacementReport.EntityType.Should().Be(nameof(Inspection));
        replacementReport.EntityId.Should().Be(created.Id);
        var reportCountAfterRecovery = await _db.StoredFiles.AsNoTracking()
            .CountAsync(file => file.EntityType == nameof(Inspection) && file.EntityId == created.Id);
        var outboxCountAfterRecovery = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.IdempotencyKey.Contains(recoveryKey));
        var auditCountAfterRecovery = await _db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(log => log.CommandIdempotencyKey.Contains(recoveryKey));

        var (replayed, replayError) = await _service.RecoverChronologyAuthorizedAsync(
            _scope, created.Id, request, recoveryKey);

        replayError.Should().BeNull();
        replayed.Should().NotBeNull();
        replayed!.CompletedAt.Should().Be(recovered.CompletedAt);
        replayed.RetiredReportStoredFileId.Should().Be(recovered.RetiredReportStoredFileId);
        replayed.ReportStoredFileId.Should().Be(recovered.ReportStoredFileId);
        (await _db.StoredFiles.AsNoTracking()
            .CountAsync(file => file.EntityType == nameof(Inspection) && file.EntityId == created.Id))
            .Should().Be(reportCountAfterRecovery);
        (await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.IdempotencyKey.Contains(recoveryKey)))
            .Should().Be(outboxCountAfterRecovery);
        (await _db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(log => log.CommandIdempotencyKey.Contains(recoveryKey)))
            .Should().Be(auditCountAfterRecovery);
    }

    [Fact]
    public async Task RecoverChronologyAuthorizedAsync_ExpectedStateMismatchDoesNotMutate()
    {
        var originalBusinessNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var correctedCompletedAtUtc = DateTime.SpecifyKind(
            originalBusinessNowUtc.AddDays(-2),
            DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(originalBusinessNowUtc));
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = originalBusinessNowUtc,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var firstItem = created!.Items.OrderBy(item => item.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, firstItem.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Recovery setup mutation" }, NextOperationKey());
        var (completed, completeError) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: NextOperationKey());
        completeError.Should().BeNull();
        completed.Should().NotBeNull();
        completed!.ReportStoredFileId.Should().NotBeNull();
        var reportId = completed.ReportStoredFileId.Value;
        var before = await _db.Inspections.AsNoTracking().SingleAsync(inspection => inspection.Id == created.Id);
        var beforeReport = await _db.StoredFiles.AsNoTracking().SingleAsync(file => file.Id == reportId);
        var outboxCountBefore = await _db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey.Contains("inspection-chronology-recovery-mismatch"));

        clock.SetUtcNow(new DateTimeOffset(correctedCompletedAtUtc));
        var (result, error) = await _service.RecoverChronologyAuthorizedAsync(
            _scope,
            created.Id,
            new RecoverInspectionChronologyRequest
            {
                ExpectedContaminatedCompletedAtUtc = before.CompletedAt!.Value.AddMinutes(1),
                ExpectedContaminatedReportStoredFileId = reportId,
                ExpectedContaminatedReportUploadedAtUtc = beforeReport.UploadedAt,
                CorrectCompletedAtUtc = correctedCompletedAtUtc,
            },
            "inspection-chronology-recovery-mismatch");

        result.Should().BeNull();
        error.Should().Be("Inspection chronology recovery expected-state check failed; no rows were changed.");
        var after = await _db.Inspections.AsNoTracking().SingleAsync(inspection => inspection.Id == created.Id);
        after.CompletedAt.Should().Be(before.CompletedAt);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.ReportStoredFileId.Should().Be(before.ReportStoredFileId);
        var afterReport = await _db.StoredFiles.AsNoTracking().SingleAsync(file => file.Id == reportId);
        afterReport.UploadedAt.Should().Be(beforeReport.UploadedAt);
        afterReport.DeletedAt.Should().BeNull();
        (await _db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey.Contains("inspection-chronology-recovery-mismatch")))
            .Should().Be(outboxCountBefore);
    }

    [Fact]
    public async Task UpdateItemAuthorizedAsync_AllowsRealItemMutationAtSameFrozenInstant()
    {
        var frozenNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(frozenNowUtc));
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = frozenNowUtc,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var item = created!.Items.OrderBy(candidate => candidate.SortOrder).First();
        var updateKey = "inspection-item-same-instant-real-mutation";

        var updated = await _service.UpdateItemAuthorizedAsync(
            _scope,
            created.Id,
            item.Id,
            new UpdateInspectionItemRequest
            {
                Result = InspectionItemResult.Fail,
                Note = "Same-instant mutation",
            },
            updateKey);

        updated.Should().NotBeNull();
        updated!.Result.Should().Be(InspectionItemResult.Fail);
        updated.Note.Should().Be("Same-instant mutation");
        var inspection = await _db.Inspections.AsNoTracking().SingleAsync(row => row.Id == created.Id);
        inspection.UpdatedAt.Should().Be(frozenNowUtc);
        (await _db.AtomicAuditLogs.AsNoTracking().CountAsync(log =>
            log.CommandIdempotencyKey.Contains(updateKey) && log.EntityType == nameof(InspectionItem)))
            .Should().Be(1);
        (await _db.AtomicAuditLogs.AsNoTracking().CountAsync(log =>
            log.CommandIdempotencyKey.Contains(updateKey) && log.EntityType == nameof(Inspection)))
            .Should().Be(0);
        (await _db.OutboxMessages.AsNoTracking().CountAsync(message =>
            message.IdempotencyKey.Contains(updateKey) && message.IdempotencyKey.EndsWith(":item")))
            .Should().Be(1);
        (await _db.OutboxMessages.AsNoTracking().CountAsync(message =>
            message.IdempotencyKey.Contains(updateKey) && message.IdempotencyKey.EndsWith(":inspection")))
            .Should().Be(0);
    }

    [Fact]
    public async Task UpdateItemAuthorizedAsync_ExactStateFreshKeyNoOpReplaysWithoutAuditOrOutbox()
    {
        var frozenNowUtc = DateTime.SpecifyKind(
            DateTime.UtcNow.AddMinutes(10),
            DateTimeKind.Utc);
        var clock = new MutableTimeProvider(new DateTimeOffset(frozenNowUtc));
        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            NullLogger<InspectionService>.Instance,
            clock,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = frozenNowUtc,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();
        var item = created!.Items.OrderBy(candidate => candidate.SortOrder).First();
        item.Result.Should().Be(InspectionItemResult.Pending);
        item.Note.Should().BeNull();
        var noopKey = "inspection-item-exact-state-noop";
        var request = new UpdateInspectionItemRequest { Result = InspectionItemResult.Pending };

        var first = await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id, request, noopKey);
        var replayed = await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id, request, noopKey);

        first.Should().NotBeNull();
        replayed.Should().NotBeNull();
        first!.Id.Should().Be(item.Id);
        replayed!.Id.Should().Be(item.Id);
        first.Result.Should().Be(InspectionItemResult.Pending);
        replayed.Result.Should().Be(InspectionItemResult.Pending);
        (await _db.AtomicAuditLogs.AsNoTracking().CountAsync(log =>
            log.CommandIdempotencyKey.Contains(noopKey)))
            .Should().Be(0);
        (await _db.OutboxMessages.AsNoTracking().CountAsync(message =>
            message.IdempotencyKey.Contains(noopKey)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Complete_BatchesFailedItemWorkOrderCreationWithoutPerItemScopeQueries()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var items = created!.Items.OrderBy(i => i.SortOrder).Take(3).ToList();
        foreach (var item in items)
        {
            await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
                new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = $"Fail {item.Id}" }, NextOperationKey());
        }

        _executedSql.Clear();
        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-batches-work-orders");

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.CreatedWorkOrderIds.Should().HaveCount(3);

        var propertyScopeChecks = _executedSql.Count(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
        propertyScopeChecks.Should().BeLessThanOrEqualTo(3,
            "report generation has three bounded DB-side projections and must not add a query per failed item");

        var workOrderHydrationReads = _executedSql.Count(sql =>
            sql.Contains("FROM \"WorkOrders\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase));
        workOrderHydrationReads.Should().BeLessThanOrEqualTo(1,
            "broadcast payloads should be hydrated with one projection instead of one read per created work order");
    }

    [Fact]
    public async Task Complete_AlreadyCompleted_ReturnsError()
    {
        var property = SeedProperty();
        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            ScheduledFor = DateTime.UtcNow,
        }, NextOperationKey());

        await _service.CompleteAuthorizedAsync(
            _scope, created!.Id, userId: 1, operationKey: "complete-already-completed-first");
        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 1, operationKey: "complete-already-completed-second");

        summary.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public async Task CompletedInspection_BlocksChecklistItemEdits()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var item = created!.Items.OrderBy(i => i.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass, Note = "Walked before completion" }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-blocks-item-edits");
        error.Should().BeNull();
        summary.Should().NotBeNull();

        Func<Task> edit = () => _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Late edit" }, NextOperationKey());

        var ex = await edit.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("completed inspection");

        var persisted = await _db.InspectionItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        persisted.Result.Should().Be(InspectionItemResult.Pass);
        persisted.Note.Should().Be("Walked before completion");
    }

    [Fact]
    public async Task CompletedInspection_BlocksChecklistQuestionStructureChanges()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var items = created!.Items.OrderBy(i => i.SortOrder).Take(2).ToList();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-blocks-structure-changes");
        error.Should().BeNull();
        summary.Should().NotBeNull();

        Func<Task> add = () => _service.CreateItemAuthorizedAsync(_scope, created.Id, new CreateInspectionItemRequest
        {
            Area = "Safety",
            Label = "New late question",
        }, NextOperationKey());
        Func<Task> editText = () => _service.UpdateItemAuthorizedAsync(_scope, created.Id, items[0].Id, new UpdateInspectionItemRequest
        {
            Label = "Changed after completion",
        }, NextOperationKey());
        Func<Task> delete = () => _service.DeleteItemAuthorizedAsync(
            _scope, created.Id, items[0].Id, NextOperationKey());
        Func<Task> reorder = () => _service.ReorderItemsAuthorizedAsync(_scope, created.Id, new ReorderInspectionItemsRequest
        {
            ItemIds = created.Items.Select(i => i.Id).Reverse().ToList(),
        }, NextOperationKey());

        foreach (var action in new[] { add, editText, delete, reorder })
        {
            var ex = await action.Should().ThrowAsync<DomainValidationException>();
            ex.Which.StatusCode.Should().Be(409);
            ex.Which.Message.Should().Contain("completed inspection");
        }

        var persisted = await _db.InspectionItems.AsNoTracking()
            .Where(i => i.InspectionId == created.Id)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
        persisted.Should().HaveCount(created.Items.Count);
        persisted[0].Label.Should().Be(items[0].Label);
    }

    [Fact]
    public async Task CompletedInspection_BlocksChecklistPhotoAttachment()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var item = created!.Items.OrderBy(i => i.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-blocks-photo-attachment");
        error.Should().BeNull();
        summary.Should().NotBeNull();

        var file = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "inspection-photo.jpg",
            FilePath = "inspection-photo.jpg",
            ContentType = "image/jpeg",
            FileSize = 12,
            EntityType = "Inspection",
            EntityId = created.Id,
            UploadedAt = DateTime.UtcNow,
        };
        _db.StoredFiles.Add(file);
        await _db.SaveChangesAsync();

        Func<Task> attachPhoto = () => _service.AttachItemPhotoAuthorizedAsync(
            _scope, created.Id, item.Id, file.Id, NextOperationKey());

        var ex = await attachPhoto.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("completed inspection");

        var persisted = await _db.InspectionItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        persisted.PhotoStoredFileId.Should().BeNull();
    }

    [Fact]
    public async Task CompletedInspection_AttachMissingPhoto_RemainsReadOnly()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAuthorizedAsync(_scope, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = moveIn.Id,
        }, NextOperationKey());
        created.Should().NotBeNull();

        var item = created!.Items.OrderBy(i => i.SortOrder).First();
        await _service.UpdateItemAuthorizedAsync(_scope, created.Id, item.Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass }, NextOperationKey());

        var (summary, error) = await _service.CompleteAuthorizedAsync(
            _scope, created.Id, userId: 7, operationKey: "complete-missing-photo-case");
        error.Should().BeNull();
        summary.Should().NotBeNull();

        Func<Task> attachMissingPhoto = () => _service.AttachItemPhotoAuthorizedAsync(
            _scope, created.Id, item.Id, storedFileId: 999_999, operationKey: NextOperationKey());

        var ex = await attachMissingPhoto.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("completed inspection");

        var persisted = await _db.InspectionItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        persisted.PhotoStoredFileId.Should().BeNull();
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private void SeedInspection(Property property, DateTime scheduledFor, string outcome)
    {
        _db.Inspections.Add(new Inspection
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Type = InspectionType.Routine,
            Status = InspectionStatus.Scheduled,
            ScheduledFor = scheduledFor,
            Outcome = outcome,
            CreatedAt = scheduledFor,
            UpdatedAt = scheduledFor,
        });
        _db.SaveChanges();
    }

    private sealed class NoopInspectionDataUpdate : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? LastError { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                LastError = exception ?? new InvalidOperationException(formatter(state, exception));
        }
    }

    /// <summary>In-memory <see cref="IFileStorage"/> so report generation works without disk.</summary>
    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var key = $"{Guid.NewGuid():N}_{fileName}";
            _files[key] = ms.ToArray();
            return key;
        }

        public async Task UploadAtAsync(
            Stream content,
            string storagePath,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            _files[storagePath] = ms.ToArray();
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class CleanupRaceFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Func<string, Task>? BeforeUploadAsync { get; set; }
        public List<PendingFileUploadCleanupClaim> Claims { get; } = [];
        public int UploadCount { get; private set; }

        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task UploadAtAsync(
            Stream content,
            string storagePath,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            if (BeforeUploadAsync is not null)
                await BeforeUploadAsync(storagePath);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            _files[storagePath] = buffer.ToArray();
            UploadCount++;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            _files.TryGetValue(path, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException(path);

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class DeterministicInspectionPdfGenerator : IInspectionReportPdfGenerator
    {
        public byte[] Generate(InspectionReportData data) =>
            Encoding.UTF8.GetBytes($"{data.Type}|{data.CompletedAt:O}|{data.FailCount}|{data.PassCount}");
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

}
