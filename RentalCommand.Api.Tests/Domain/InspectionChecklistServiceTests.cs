using FluentAssertions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
