using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

/// <summary>
/// Unit tests for <see cref="ScanService"/> using SQLite in-memory (required because
/// <c>ExecuteUpdateAsync</c> is not supported by the EF InMemory provider).
/// Each test opens its own connection so the schema is isolated.
/// </summary>
public class ScanServiceTests : IDisposable
{
    // Shared portfolio id used by all seeds in a test.
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly Mock<IScanFileService> _filesMock;
    private readonly RecordingExpenseService _expenses;
    private readonly Mock<IPaymentService> _paymentsMock;
    private readonly RecordingWorkOrderService _workOrders;
    private readonly RecordingLeaseService _leases;
    private readonly RecordingTenantService _tenants;
    private readonly RecordingPropertyService _properties;
    private readonly RecordingUnitService _units;
    private readonly RecordingAuditService _audit;
    private readonly ScanService _sut;

    public ScanServiceTests()
    {
        // Keep the connection open for the lifetime of the test so the in-memory DB persists.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new RentalCommandTestDbContext(options);
        _db.Database.EnsureCreated();

        // Seed a portfolio row (FK required by ScanDraft + StoredFile).
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _filesMock    = new Mock<IScanFileService>(MockBehavior.Strict);
        _expenses     = new RecordingExpenseService();
        _paymentsMock = new Mock<IPaymentService>();
        _workOrders   = new RecordingWorkOrderService();
        _leases       = new RecordingLeaseService();
        _tenants      = new RecordingTenantService(_db);
        _properties   = new RecordingPropertyService(_db);
        _units        = new RecordingUnitService(_db);
        _audit        = new RecordingAuditService();

        _sut = new ScanService(
            _db,
            _filesMock.Object,
            _expenses,
            _paymentsMock.Object,
            _workOrders,
            _leases,
            _tenants,
            _properties,
            _units,
            // Existing tests don't exercise the Application confirm path; a default mock satisfies the ctor.
            new Mock<IApplicationService>().Object,
            _audit,
            NullLogger<ScanService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // -------------------------------------------------------------------------
    // Confirm: happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingExpenseDraft_SucceedsAndReKeysStoredFile()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"ACME","confidence":0.9},"amount":{"value":"42.50","confidence":0.8},"transaction_date":{"value":"2026-01-15","confidence":0.95},"category":{"value":"Repairs","confidence":0.7},"notes":{"value":"","confidence":0.0},"payment_method":{"value":"Visa","confidence":0.8},"card_last4":{"value":"4242","confidence":0.8},"document_kind":{"value":"Receipt","confidence":0.9},"line_items":{"value":"[{\"description\":\"Washer hose\",\"quantity\":2,\"unit_price\":6.50,\"amount\":13.00},{\"description\":\"Pipe tape\",\"amount\":3.00}]","confidence":0.7}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        var file  = SeedStoredFile(draft.FilePath);

        const int fixedExpenseId = 99;
        _expenses.SetupResponse(new ExpenseResponse { Id = fixedExpenseId, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        // If the result failed, expose the error message to aid debugging.
        result.Error.Should().BeNull("ScanService returned an error: " + result.Error);
        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.CreatedEntityId.Should().Be(fixedExpenseId);

        // Typed scalar columns + line items are threaded into the create request from the extraction.
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.PaymentMethod.Should().Be("Visa");
        _expenses.LastRequest.CardLast4.Should().Be("4242");
        _expenses.LastRequest.DocumentKind.Should().Be("Receipt");
        _expenses.LastRequest.LineItems.Should().HaveCount(2);
        var firstLine = _expenses.LastRequest.LineItems[0];
        firstLine.Description.Should().Be("Washer hose");
        firstLine.Quantity.Should().Be(2m);
        firstLine.UnitPrice.Should().Be(6.50m);
        firstLine.Amount.Should().Be(13.00m);
        firstLine.LineNumber.Should().Be(1);
        _expenses.LastRequest.LineItems[1].LineNumber.Should().Be(2);

        // Draft should be Confirmed. Query the DB directly via raw SQL on the shared connection,
        // bypassing EF's change tracker entirely.
        string? draftStatus;
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT Status FROM ScanDrafts WHERE Id = {draft.Id}";
            draftStatus = (string?)cmd.ExecuteScalar();
        }
        draftStatus.Should().Be("Confirmed");

        string? storedFileEntityType;
        int? storedFileEntityId;
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT EntityType, EntityId FROM StoredFiles WHERE Id = {file.Id}";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            storedFileEntityType = reader.IsDBNull(0) ? null : reader.GetString(0);
            storedFileEntityId   = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
        }
        storedFileEntityType.Should().Be("Expense");
        storedFileEntityId.Should().Be(fixedExpenseId);

        // Audit log should have been called once with Created.
        _audit.Calls.Should().HaveCount(1);
        _audit.Calls[0].operation.Should().Be(AuditLogOperation.Created);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingExpenseDraft_WithExistingVendorName_LinksVendor()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"clearline plumbing","confidence":0.92},"amount":{"value":"286.45","confidence":0.9},"transaction_date":{"value":"2026-06-22","confidence":0.9},"document_kind":{"value":"Receipt","confidence":0.9}}""";

        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Clearline Plumbing",
            ServiceType = "Plumbing",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Vendors.Add(vendor);
        await _db.SaveChangesAsync();

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 100, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.VendorId.Should().Be(vendor.Id);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingExpenseDraft_WithNewVendorName_CreatesAndLinksVendor()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"Franklin Hardware Supply","confidence":0.94},"vendor_phone":{"value":"614-555-0188","confidence":0.86},"vendor_tax_id":{"value":"12-3456789","confidence":0.81},"amount":{"value":"60.94","confidence":0.95},"transaction_date":{"value":"2026-07-03","confidence":0.9},"document_kind":{"value":"Receipt","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 102, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);

        var vendor = await _db.Vendors.AsNoTracking().SingleAsync(v => v.PortfolioId == PortfolioId);
        vendor.Name.Should().Be("Franklin Hardware Supply");
        vendor.ServiceType.Should().Be("General");
        vendor.Phone.Should().Be("614-555-0188");
        vendor.TaxId.Should().Be("12-3456789");

        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.VendorId.Should().Be(vendor.Id);
        _expenses.LastRequest.Description.Should().Be("Franklin Hardware Supply");
    }

    // -------------------------------------------------------------------------
    // Confirm: overrides win
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_WithAmountOverride_UsesOverrideAmount()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"Old Vendor","confidence":0.5},"amount":{"value":"10.00","confidence":0.5},"transaction_date":{"value":"2026-01-01","confidence":0.5},"category":{"value":"Other","confidence":0.5},"notes":{"value":"","confidence":0.0}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);

        _expenses.SetupResponse(new ExpenseResponse { Id = 55, PortfolioId = PortfolioId });

        var overrides = """{"amount":99.99}""";
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 1, overridesJson: overrides);

        result.Success.Should().BeTrue();

        // The request that reached IExpenseService should have the overridden amount.
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.Amount.Should().Be(99.99m);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_ExpenseDraft_WithUnitAndWorkOrderOverrides_PassesAssociationsToExpenseCreate()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"ComfortZone HVAC","confidence":0.9},"amount":{"value":"232.50","confidence":0.9},"transaction_date":{"value":"2026-06-22","confidence":0.9},"category":{"value":"Repairs","confidence":0.8},"document_kind":{"value":"Receipt","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 101, PortfolioId = PortfolioId });

        var overrides = """{"propertyId":10,"unitId":20,"workOrderId":30}""";
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.PropertyId.Should().Be(10);
        _expenses.LastRequest.UnitId.Should().Be(20);
        _expenses.LastRequest.WorkOrderId.Should().Be(30);
    }

    // -------------------------------------------------------------------------
    // Confirm: snake_case vendor/date overrides are honored (regression guard)
    // The review UI keys edits by the extraction field names (vendor_name,
    // transaction_date); ApplyOverrides must apply them or a corrected vendor/date
    // is silently dropped — the exact trust-breaking bug for this human gate.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_WithSnakeCaseVendorAndDateOverrides_AppliesThem()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"Old Vendor","confidence":0.4},"amount":{"value":"10.00","confidence":0.9},"transaction_date":{"value":"2026-01-01","confidence":0.4},"category":{"value":"Other","confidence":0.9},"notes":{"value":"","confidence":0.0}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 77, PortfolioId = PortfolioId });

        var overrides = """{"vendor_name":"Corrected Vendor","transaction_date":"2026-03-20"}""";
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 2, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.Description.Should().Be("Corrected Vendor");
        _expenses.LastRequest.IncurredAt.Should().Be(new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    // -------------------------------------------------------------------------
    // Confirm: edited line items win over the extracted ones (TSK-29).
    // The review UI now makes the line-items table editable and sends the full
    // edited array under "line_items"; the persisted Expense must carry the edited
    // rows (re-numbered 1..n), not the originally extracted ones.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_WithEditedLineItemsOverride_PersistsEditedItems()
    {
        // Extraction found two rows; the reviewer corrects them down to a single edited row.
        const string extractedJson =
            """{"vendor_name":{"value":"ACME","confidence":0.9},"amount":{"value":"42.50","confidence":0.8},"line_items":{"value":"[{\"description\":\"Washer hose\",\"quantity\":2,\"unit_price\":6.50,\"amount\":13.00},{\"description\":\"Pipe tape\",\"amount\":3.00}]","confidence":0.7}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 88, PortfolioId = PortfolioId });

        // The web sends the edited rows as a real JSON array (snake_case item keys).
        var overrides =
            """{"line_items":[{"description":"Corrected hose","quantity":3,"unit_price":7.00,"amount":21.00}]}""";

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 4, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.LineItems.Should().HaveCount(1);
        var line = _expenses.LastRequest.LineItems[0];
        line.Description.Should().Be("Corrected hose");
        line.Quantity.Should().Be(3m);
        line.UnitPrice.Should().Be(7.00m);
        line.Amount.Should().Be(21.00m);
        line.LineNumber.Should().Be(1);
    }

    // Clearing every row in the editable table sends an empty "line_items" array,
    // which must remove the extracted rows entirely (not silently keep them).
    [Fact]
    public async Task ConfirmAndCreateAsync_WithEmptyLineItemsOverride_PersistsNoItems()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"ACME","confidence":0.9},"amount":{"value":"42.50","confidence":0.8},"line_items":{"value":"[{\"description\":\"Washer hose\",\"quantity\":2,\"unit_price\":6.50,\"amount\":13.00}]","confidence":0.7}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 89, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(
            PortfolioId, draft.Id, userId: 4, overridesJson: """{"line_items":[]}""");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.LineItems.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Confirm: already confirmed draft returns failure (idempotency guard)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_AlreadyConfirmed_ReturnsFalse()
    {
        var draft = SeedDraft("Confirmed", extractedFields: null);

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 1, overridesJson: "{}");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        _expenses.LastRequest.Should().BeNull(); // expense service was never called
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingWorkOrderDraft_CreatesWorkOrder()
    {
        const string extractedJson =
            """{"target_entity_type":{"value":"WorkOrder","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.7},"title":{"value":"Ceiling leak","confidence":0.9},"description":{"value":"Tenant says water is coming through the kitchen ceiling.","confidence":0.85},"category":{"value":"Plumbing","confidence":0.8},"priority":{"value":"Emergency","confidence":0.8},"estimated_cost":{"value":"250.00","confidence":0.4}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "WorkOrder");
        SeedStoredFile(draft.FilePath);
        // The extracted property_id/unit_id are now re-validated against real in-portfolio rows
        // (a hallucinated/cross-portfolio id is dropped to manual selection), so the grounded ids
        // the draft carries must correspond to actual rows in this portfolio.
        _db.Properties.Add(new Property
        {
            Id = 10,
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Units.Add(new Unit
        {
            Id = 20,
            PropertyId = 10,
            UnitNumber = "1",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _workOrders.SetupResponse(new WorkOrderResponse { Id = 123, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.EntityType.Should().Be("WorkOrder");
        result.CreatedEntityId.Should().Be(123);
        _workOrders.LastRequest.Should().NotBeNull();
        _workOrders.LastRequest!.PropertyId.Should().Be(10);
        _workOrders.LastRequest.UnitId.Should().Be(20);
        _workOrders.LastRequest.Title.Should().Be("Ceiling leak");
        _workOrders.LastRequest.Description.Should().Contain("kitchen ceiling");
        _workOrders.LastRequest.Category.Should().Be("Plumbing");
        _workOrders.LastRequest.Priority.Should().Be(WorkOrderPriority.Emergency);
        _workOrders.LastRequest.EstimatedCost.Should().Be(250.00m);

        string? draftStatus;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT Status FROM ScanDrafts WHERE Id = {draft.Id}";
            draftStatus = (string?)cmd.ExecuteScalar();
        }
        draftStatus.Should().Be("Confirmed");
        _audit.Calls.Should().Contain(c => c.entityType == "WorkOrder" && c.entityId == 123);
    }

    // -------------------------------------------------------------------------
    // Confirm: a scanned lease document becomes a Lease with the extracted terms, the unit
    // linked, and a chained Tenant created from the extracted name (the "import your
    // PDF leases" migration unlock).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingLeaseDraft_CreatesLeaseWithTermsAndChainsTenant()
    {
        const string extractedJson =
            """{"target_entity_type":{"value":"Lease","confidence":0.95},"tenant_name":{"value":"Marcus Williams","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"lease_number":{"value":"L-2026-7","confidence":0.8},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2026-12-31","confidence":0.9},"monthly_rent":{"value":"1450.00","confidence":0.9},"security_deposit":{"value":"1450.00","confidence":0.8},"late_fee":{"value":"75.00","confidence":0.7},"rent_due_day":{"value":"1","confidence":0.8}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 321, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.EntityType.Should().Be("Lease");
        result.CreatedEntityId.Should().Be(321);

        _leases.LastRequest.Should().NotBeNull();
        var req = _leases.LastRequest!;
        req.PropertyId.Should().Be(10);
        req.UnitId.Should().Be(20);
        req.LeaseNumber.Should().Be("L-2026-7");
        req.StartDate.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        req.EndDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        req.MonthlyRent.Should().Be(1450.00m);
        req.SecurityDeposit.Should().Be(1450.00m);
        req.LateFeeAmount.Should().Be(75.00m);
        req.RentDueDay.Should().Be(1);
        req.Status.Should().Be(LeaseStatus.Active);
        req.Notes.Should().Be("Imported from scanned lease document.");

        // A chained Tenant was created from the extracted name and its id linked on the lease.
        _tenants.LastRequest.Should().NotBeNull();
        _tenants.LastRequest!.FirstName.Should().Be("Marcus");
        _tenants.LastRequest.LastName.Should().Be("Williams");
        var createdTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.PortfolioId == PortfolioId);
        createdTenant.Should().NotBeNull();
        req.TenantId.Should().Be(createdTenant!.Id);

        // Draft confirmed + source document re-keyed to the lease.
        string? draftStatus;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT Status FROM ScanDrafts WHERE Id = {draft.Id}";
            draftStatus = (string?)cmd.ExecuteScalar();
        }
        draftStatus.Should().Be("Confirmed");
        _audit.Calls.Should().Contain(c => c.entityType == "Lease" && c.entityId == 321);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDraftWithExistingTenantNameMatch_ReusesTenant()
    {
        // Seed an existing tenant whose full name matches the extracted name (case-insensitive).
        _db.Tenants.Add(new Tenant
        {
            Id = 50,
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        const string extractedJson =
            """{"tenant_name":{"value":"marcus williams","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2026-12-31","confidence":0.9},"monthly_rent":{"value":"1450.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 322, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _leases.LastRequest!.TenantId.Should().Be(50);
        // No new tenant was created — the existing one was matched.
        _tenants.LastRequest.Should().BeNull();
        (await _db.Tenants.CountAsync(t => t.PortfolioId == PortfolioId)).Should().Be(1);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDraftWithTenantContactOverrides_PersistsCreatedTenantContact()
    {
        const string extractedJson =
            """{"tenant_name":{"value":"Maria Chen","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"start_date":{"value":"2026-06-01","confidence":0.9},"end_date":{"value":"2027-05-31","confidence":0.9},"monthly_rent":{"value":"1500.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 324, PortfolioId = PortfolioId });

        var overrides = """
            {
                "tenantEmail": "maria.chen@example.local",
                "tenantPhone": "555-010-3970",
                "tenantEmergencyContact": "Sam Chen 555-010-3971"
            }
            """;
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _tenants.LastRequest.Should().NotBeNull();
        _tenants.LastRequest!.Email.Should().Be("maria.chen@example.local");
        _tenants.LastRequest.Phone.Should().Be("555-010-3970");
        _tenants.LastRequest.EmergencyContact.Should().Be("Sam Chen 555-010-3971");

        var createdTenant = await _db.Tenants.SingleAsync(t => t.PortfolioId == PortfolioId);
        createdTenant.Email.Should().Be("maria.chen@example.local");
        createdTenant.Phone.Should().Be("555-010-3970");
        createdTenant.EmergencyContact.Should().Be("Sam Chen 555-010-3971");
        _leases.LastRequest!.TenantId.Should().Be(createdTenant.Id);
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDraftWithNullUnitOverride_CreatesReviewerEditedUnit()
    {
        const string extractedJson =
            """{"tenant_name":{"value":"Lena Park","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"unit_number":{"value":"1","confidence":0.85},"start_date":{"value":"2026-06-01","confidence":0.9},"end_date":{"value":"2027-05-31","confidence":0.9},"monthly_rent":{"value":"1500.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 325, PortfolioId = PortfolioId });

        var overrides = """
            {
                "propertyId": 10,
                "unitId": null,
                "unitNumber": "5C",
                "unitBedrooms": 2,
                "unitBathrooms": 1.5,
                "tenantName": "Lena Park",
                "tenantEmail": "lena.park@example.local",
                "tenantPhone": "555-010-3972",
                "tenantEmergencyContact": "Noah Park 555-010-3973",
                "leaseNumber": "L-5C-2026-06"
            }
            """;

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        var reviewerUnit = await _db.Units.SingleAsync(u => u.UnitNumber == "5C");
        reviewerUnit.PropertyId.Should().Be(10);
        reviewerUnit.Bedrooms.Should().Be(2m);
        reviewerUnit.Bathrooms.Should().Be(1.5m);
        _leases.LastRequest.Should().NotBeNull();
        _leases.LastRequest!.UnitId.Should().Be(reviewerUnit.Id);
        _leases.LastRequest.UnitId.Should().NotBe(20);
        _leases.LastRequest.LeaseNumber.Should().Be("L-5C-2026-06");
        _tenants.LastRequest!.Email.Should().Be("lena.park@example.local");
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDraftWithForeignPropertyId_IsRejected()
    {
        // property_id 999 is NOT in this portfolio: it must be dropped to 0 (IDOR guard), and with no
        // override property the confirm fails rather than linking a foreign property.
        const string extractedJson =
            """{"tenant_name":{"value":"Jane Doe","confidence":0.9},"property_id":{"value":"999","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2026-12-31","confidence":0.9},"monthly_rent":{"value":"1000.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 999, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("property");
        _leases.LastRequest.Should().BeNull(); // lease service was never called
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDraftWithOverrideTenantId_UsesOverrideTenant()
    {
        // A tenant the reviewer selected via overrides wins; it must be validated in-portfolio.
        _db.Tenants.Add(new Tenant
        {
            Id = 60,
            PortfolioId = PortfolioId,
            FirstName = "Selected",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        const string extractedJson =
            """{"tenant_name":{"value":"Someone Else","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2026-12-31","confidence":0.9},"monthly_rent":{"value":"1200.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.SetupResponse(new LeaseResponse { Id = 323, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(
            PortfolioId, draft.Id, userId: 7, overridesJson: """{"tenantId":60}""");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _leases.LastRequest!.TenantId.Should().Be(60);
        // The override tenant was used, so no chained tenant was created.
        _tenants.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseDomainValidation_ReturnsSpecificUserMessage()
    {
        const string error =
            "This unit already has an active lease (QA-2026-006-2B) overlapping these dates.";
        const string extractedJson =
            """{"tenant_name":{"value":"Riley Patel","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.85},"lease_number":{"value":"L-2026-8","confidence":0.8},"start_date":{"value":"2026-02-01","confidence":0.9},"end_date":{"value":"2027-02-01","confidence":0.9},"monthly_rent":{"value":"1200.00","confidence":0.9}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft.FilePath);
        SeedPropertyAndUnit();
        _leases.ThrowOnCreate(new DomainValidationException(error));

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    // -------------------------------------------------------------------------
    // Scan-import bootstrap (the load-bearing invariant): scanning a lease into an EMPTY
    // portfolio creates Property → Unit → Tenant → Lease from the document; a SECOND scan
    // for the same address links to the existing Property/Unit rather than duplicating them.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_LeaseIntoEmptyPortfolio_CreatesPropertyUnitTenantAndLease_ThenDedupesOnRescan()
    {
        // No property_id / unit_id — a brand-new landlord with an EMPTY portfolio. The leased premises
        // (address + unit) and the tenant come straight off the document.
        const string extractedJson =
            """
            {"target_entity_type":{"value":"Lease","confidence":0.95},
             "tenant_name":{"value":"Dana Brooks","confidence":0.9},
             "property_name":{"value":"Riverside Flats","confidence":0.7},
             "property_address":{"value":"742 Evergreen St","confidence":0.9},
             "property_city":{"value":"Springfield","confidence":0.9},
             "property_state":{"value":"OH","confidence":0.9},
             "property_postal_code":{"value":"45503","confidence":0.9},
             "unit_number":{"value":"3C","confidence":0.85},
             "unit_bedrooms":{"value":"2","confidence":0.7},
             "unit_bathrooms":{"value":"1.5","confidence":0.7},
             "start_date":{"value":"2026-02-01","confidence":0.9},
             "end_date":{"value":"2027-01-31","confidence":0.9},
             "monthly_rent":{"value":"1325.00","confidence":0.9}}
            """;

        var draft1 = SeedDraft("Reviewing", extractedJson, targetEntityType: "Lease");
        SeedStoredFile(draft1.FilePath);
        _leases.SetupResponse(new LeaseResponse { Id = 901, PortfolioId = PortfolioId });

        // Sanity: the portfolio really is empty before the scan.
        (await _db.Properties.CountAsync()).Should().Be(0);
        (await _db.Units.CountAsync()).Should().Be(0);
        (await _db.Tenants.CountAsync()).Should().Be(0);

        var result1 = await _sut.ConfirmAndCreateAsync(PortfolioId, draft1.Id, userId: 5, overridesJson: "{}");

        result1.Success.Should().BeTrue("Unexpected: " + result1.Error);
        result1.EntityType.Should().Be("Lease");

        // A Property, a Unit, and a Tenant were all created from the document.
        var property = await _db.Properties.SingleAsync();
        property.AddressLine1.Should().Be("742 Evergreen St");
        property.City.Should().Be("Springfield");
        property.State.Should().Be("OH");
        property.PostalCode.Should().Be("45503");
        property.Name.Should().Be("Riverside Flats");
        property.Notes.Should().Be("Created from scanned lease document.");

        var unit = await _db.Units.SingleAsync();
        unit.PropertyId.Should().Be(property.Id);
        unit.UnitNumber.Should().Be("3C");
        unit.Bedrooms.Should().Be(2m);
        unit.Bathrooms.Should().Be(1.5m);
        unit.Notes.Should().Be("Created from scanned lease document.");

        var tenant = await _db.Tenants.SingleAsync();
        tenant.FirstName.Should().Be("Dana");
        tenant.LastName.Should().Be("Brooks");
        tenant.Notes.Should().Be("Created from scanned lease document.");

        // The Lease that was created links the freshly-created property, unit, and tenant.
        var leaseReq1 = _leases.LastRequest!;
        leaseReq1.PropertyId.Should().Be(property.Id);
        leaseReq1.UnitId.Should().Be(unit.Id);
        leaseReq1.TenantId.Should().Be(tenant.Id);
        leaseReq1.MonthlyRent.Should().Be(1325.00m);
        leaseReq1.Notes.Should().Be("Imported from scanned lease document.");

        _properties.CreateCount.Should().Be(1);
        _units.CreateCount.Should().Be(1);

        // ---- Second scan of the SAME premises (address formatted differently: "St" vs "Street") ----
        // It must LINK to the existing property + unit, not create duplicates (the dedupe invariant).
        const string rescanJson =
            """
            {"target_entity_type":{"value":"Lease","confidence":0.95},
             "tenant_name":{"value":"Evan Cole","confidence":0.9},
             "property_address":{"value":"742 Evergreen Street","confidence":0.9},
             "property_city":{"value":"Springfield","confidence":0.9},
             "property_state":{"value":"OH","confidence":0.9},
             "property_postal_code":{"value":"45503","confidence":0.9},
             "unit_number":{"value":"3C","confidence":0.85},
             "start_date":{"value":"2026-03-01","confidence":0.9},
             "end_date":{"value":"2027-02-28","confidence":0.9},
             "monthly_rent":{"value":"1350.00","confidence":0.9}}
            """;

        var draft2 = SeedDraft("Reviewing", rescanJson, targetEntityType: "Lease");
        SeedStoredFile(draft2.FilePath);
        _leases.SetupResponse(new LeaseResponse { Id = 902, PortfolioId = PortfolioId });

        var result2 = await _sut.ConfirmAndCreateAsync(PortfolioId, draft2.Id, userId: 5, overridesJson: "{}");

        result2.Success.Should().BeTrue("Unexpected: " + result2.Error);

        // No new Property/Unit — the second lease links the SAME rows the first scan created.
        (await _db.Properties.CountAsync()).Should().Be(1);
        (await _db.Units.CountAsync()).Should().Be(1);
        _properties.CreateCount.Should().Be(1);
        _units.CreateCount.Should().Be(1);

        var leaseReq2 = _leases.LastRequest!;
        leaseReq2.PropertyId.Should().Be(property.Id);
        leaseReq2.UnitId.Should().Be(unit.Id);
        // A different tenant on the second lease was chained as a new tenant (now two tenants total).
        (await _db.Tenants.CountAsync()).Should().Be(2);
        leaseReq2.TenantId.Should().NotBe(tenant.Id);
    }

    // -------------------------------------------------------------------------
    // Confirm: a scanned rent check becomes a paid Payment (the "scan the check"
    // money flow). MAKE-SURE-DONE-E2E: locks the RentCheck -> Payment confirm path.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingRentCheckDraft_CreatesPaidPayment()
    {
        const string extractedJson =
            """{"document_kind":{"value":"RentCheck","confidence":0.95},"payer_name":{"value":"Marcus Williams","confidence":0.9},"bank_name":{"value":"First National","confidence":0.8},"amount":{"value":"1200.00","confidence":0.9},"transaction_date":{"value":"2026-03-03","confidence":0.9},"check_number":{"value":"1487","confidence":0.8}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "Payment");
        SeedStoredFile(draft.FilePath);

        CreatePaymentRequest? captured = null;
        _paymentsMock
            .Setup(p => p.CreateAsync(PortfolioId, It.IsAny<CreatePaymentRequest>(), It.IsAny<CancellationToken>()))
            .Callback<int, CreatePaymentRequest, CancellationToken>((_, req, _) => captured = req)
            .ReturnsAsync(new PaymentResponse { Id = 555, PortfolioId = PortfolioId });

        // The review UI supplies the lease for payment routing via overrides.
        var result = await _sut.ConfirmAndCreateAsync(
            PortfolioId, draft.Id, userId: 9, overridesJson: """{"leaseId":42}""");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.EntityType.Should().Be("Payment");
        result.CreatedEntityId.Should().Be(555);

        captured.Should().NotBeNull();
        captured!.LeaseId.Should().Be(42);
        captured.PaymentType.Should().Be(PaymentType.Rent);
        captured.Status.Should().Be(PaymentStatus.Paid);
        captured.Amount.Should().Be(1200.00m);
        captured.Method.Should().Be("Check");
        captured.ExternalReference.Should().Be("1487");
        // The promoted check fields + the full extraction superset are threaded through to the payment.
        captured.PayerName.Should().Be("Marcus Williams");
        captured.CheckNumber.Should().Be("1487");
        captured.BankName.Should().Be("First National");
        captured.ExtractedData.Should().Contain("RentCheck");
    }

    // -------------------------------------------------------------------------
    // Reject: happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RejectDraftAsync_ReviewingDraft_SetsRejectedAndLogsAudit()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null);

        var rejected = await _sut.RejectDraftAsync(PortfolioId, draft.Id, userId: 3, reason: "Not a valid receipt");

        rejected.Should().BeTrue();

        var rejectedDraft = await _db.ScanDrafts.FindAsync(draft.Id);
        rejectedDraft!.Status.Should().Be("Rejected");
        rejectedDraft.ReviewedBy.Should().Be("3");

        _audit.Calls.Should().HaveCount(1);
        _audit.Calls[0].operation.Should().Be(AuditLogOperation.Rejected);
    }

    // -------------------------------------------------------------------------
    // Seed helpers
    // -------------------------------------------------------------------------

    private ScanDraft SeedDraft(string status, string? extractedFields, string targetEntityType = "Expense")
    {
        var draft = new ScanDraft
        {
            PortfolioId      = PortfolioId,
            FilePath         = $"uploads/test-{Guid.NewGuid():N}.jpg",
            TargetEntityType = targetEntityType,
            Status           = status,
            ExtractedFields  = extractedFields,
            CreatedAt        = DateTime.UtcNow,
        };
        _db.ScanDrafts.Add(draft);
        _db.SaveChanges();
        return draft;
    }

    /// <summary>Seeds property 10 + unit 20 in the test portfolio (the grounded ids the lease drafts carry).</summary>
    private void SeedPropertyAndUnit()
    {
        _db.Properties.Add(new Property
        {
            Id = 10,
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Units.Add(new Unit
        {
            Id = 20,
            PropertyId = 10,
            UnitNumber = "1",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private StoredFile SeedStoredFile(string filePath)
    {
        var file = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName    = "receipt.jpg",
            FilePath    = filePath,
            ContentType = "image/jpeg",
            FileSize    = 1024,
            EntityType  = "ScanDraft",
            EntityId    = null,
            UploadedAt  = DateTime.UtcNow,
        };
        _db.StoredFiles.Add(file);
        _db.SaveChanges();
        return file;
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    private sealed class RecordingExpenseService : IExpenseService
    {
        private ExpenseResponse? _response;

        public CreateExpenseRequest? LastRequest { get; private set; }

        public void SetupResponse(ExpenseResponse response) => _response = response;

        public Task<ExpenseResponse?> CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }

        public Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? workOrderId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<ExpenseListResponse> ListPageAsync(
            int portfolioId,
            int? propertyId,
            int? unitId,
            int? workOrderId,
            bool workOrderLinkedOnly,
            ListQuery query,
            CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    private sealed class RecordingAuditService : IAuditTrailService
    {
        public List<(int portfolioId, string entityType, int entityId, AuditLogOperation operation)> Calls { get; } = [];

        public Task LogAsync(
            int portfolioId, string entityType, int entityId, AuditLogOperation operation,
            int? userId = null, string? actorLabel = null, string? oldValues = null,
            string? newValues = null, string? changeReason = null, string? ipAddress = null,
            CancellationToken ct = default)
        {
            Calls.Add((portfolioId, entityType, entityId, operation));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkOrderService : IWorkOrderService
    {
        private WorkOrderResponse? _response;

        public CreateWorkOrderRequest? LastRequest { get; private set; }

        public void SetupResponse(WorkOrderResponse response) => _response = response;

        public Task<WorkOrderResponse?> CreateAsync(int portfolioId, CreateWorkOrderRequest request, int? changedByUserId = null, string? changedByLabel = null, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }

        public Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? unitId, int? vendorId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<WorkOrderListResponse> ListPageAsync(int portfolioId, WorkOrderListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<WorkOrderDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<WorkOrderResponse?> UpdateAsync(int portfolioId, int id, UpdateWorkOrderRequest request, int? changedByUserId = null, string? changedByLabel = null, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    private sealed class RecordingLeaseService : ILeaseService
    {
        private LeaseResponse? _response = new() { Id = 0, PortfolioId = PortfolioId };
        private Exception? _createException;

        public CreateLeaseRequest? LastRequest { get; private set; }

        public void SetupResponse(LeaseResponse? response) => _response = response;

        public void ThrowOnCreate(Exception exception) => _createException = exception;

        public Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (_createException is not null)
                throw _createException;
            return Task.FromResult(_response);
        }

        public Task<IReadOnlyList<LeaseResponse>> ListAsync(int portfolioId, int? tenantId, int? propertyId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseListResponse> ListPageAsync(int portfolioId, LeaseListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseLedgerResponse?> GetLedgerAsync(int portfolioId, int id, int? restrictToTenantId = null, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseDocumentResponse?> GenerateDocumentAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<LeaseDocumentStatusResponse?> GetDocumentStatusAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<(Stream Stream, string FileName, string ContentType)?> GetDocumentAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    /// <summary>
    /// Recording tenant service that actually persists a Tenant to the shared test DbContext so the
    /// "chained tenant" path produces a real, in-portfolio tenant id (mirrors production TenantService).
    /// </summary>
    private sealed class RecordingTenantService : ITenantService
    {
        private readonly RentalCommandDbContext _db;

        public RecordingTenantService(RentalCommandDbContext db) => _db = db;

        public CreateTenantRequest? LastRequest { get; private set; }

        public async Task<TenantResponse> CreateAsync(int portfolioId, CreateTenantRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var now = DateTime.UtcNow;
            var entity = new Tenant
            {
                PortfolioId = portfolioId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = request.Phone,
                EmergencyContact = request.EmergencyContact,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Tenants.Add(entity);
            await _db.SaveChangesAsync(ct);
            return TenantResponse.FromEntity(entity);
        }

        public Task<IReadOnlyList<TenantResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<TenantListResponse> ListPageAsync(int portfolioId, TenantListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<TenantResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<TenantResponse?> UpdateAsync(int portfolioId, int id, UpdateTenantRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    /// <summary>
    /// Recording property service that actually persists a Property to the shared test DbContext so the
    /// scan-import bootstrap creates a real, in-portfolio property whose id downstream code (and a second
    /// scan's dedupe match) can find — mirrors production PropertyService.
    /// </summary>
    private sealed class RecordingPropertyService : IPropertyService
    {
        private readonly RentalCommandDbContext _db;

        public RecordingPropertyService(RentalCommandDbContext db) => _db = db;

        public int CreateCount { get; private set; }
        public CreatePropertyRequest? LastRequest { get; private set; }

        public async Task<PropertyResponse?> CreateAsync(int portfolioId, CreatePropertyRequest request, CancellationToken ct = default)
        {
            CreateCount++;
            LastRequest = request;
            var now = DateTime.UtcNow;
            var entity = new Property
            {
                PortfolioId = portfolioId,
                Name = request.Name,
                PropertyType = request.PropertyType,
                Status = request.Status,
                AddressLine1 = request.AddressLine1,
                AddressLine2 = request.AddressLine2,
                City = request.City,
                State = request.State,
                PostalCode = request.PostalCode,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Properties.Add(entity);
            await _db.SaveChangesAsync(ct);
            return PropertyResponse.FromEntity(entity);
        }

        public Task<IReadOnlyList<PropertyResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<PropertyListResponse> ListPageAsync(int portfolioId, PropertyListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<PropertyResponse?> UpdateAsync(int portfolioId, int id, UpdatePropertyRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    /// <summary>
    /// Recording unit service that actually persists a Unit (scoped to its property) to the shared test
    /// DbContext so the scan-import bootstrap creates a real unit whose id downstream code (and a second
    /// scan's dedupe match) can find — mirrors production UnitService.
    /// </summary>
    private sealed class RecordingUnitService : IUnitService
    {
        private readonly RentalCommandDbContext _db;

        public RecordingUnitService(RentalCommandDbContext db) => _db = db;

        public int CreateCount { get; private set; }
        public CreateUnitRequest? LastRequest { get; private set; }

        public async Task<UnitResponse?> CreateAsync(int portfolioId, CreateUnitRequest request, CancellationToken ct = default)
        {
            // Mirror production scoping: the target property must be in the caller's portfolio.
            if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
                return null;

            CreateCount++;
            LastRequest = request;
            var now = DateTime.UtcNow;
            var entity = new Unit
            {
                PropertyId = request.PropertyId,
                UnitNumber = request.UnitNumber,
                FloorPlan = request.FloorPlan,
                Bedrooms = request.Bedrooms,
                Bathrooms = request.Bathrooms,
                SquareFeet = request.SquareFeet,
                MarketRent = request.MarketRent,
                Status = request.Status,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Units.Add(entity);
            await _db.SaveChangesAsync(ct);
            return UnitResponse.FromEntity(entity);
        }

        public Task<IReadOnlyList<UnitResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<IReadOnlyList<UnitHealthResponse>> ListWithHealthAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<UnitHealthListResponse> ListWithHealthPageAsync(int portfolioId, UnitHealthListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<UnitResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<UnitResponse?> UpdateAsync(int portfolioId, int id, UpdateUnitRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }
}

/// <summary>
/// Derived DbContext that overrides Postgres-specific configurations (jsonb column type,
/// check constraints) so the schema is valid on SQLite.
/// </summary>
internal sealed class RentalCommandTestDbContext : RentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // jsonb is not understood by SQLite — remap those columns to plain text.
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Payment>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<WorkOrder>().Property(e => e.ExtractedData).HasColumnType("TEXT");

        // Remove Postgres-specific jsonb from AuditLog, OutboxMessage, QueuedJob.
        modelBuilder.Entity<AuditLog>()
            .Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>()
            .Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>()
            .Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>()
            .Property(e => e.Payload).HasColumnType("TEXT");

        // Remove check constraints that SQLite cannot execute (Lease StartDate < EndDate, RentDueDay).
        // EF Core lets us replace the table builder to drop all constraints.
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
