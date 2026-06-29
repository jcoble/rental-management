using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class UnitDashboardServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly UnitDashboardService _sut;

    public UnitDashboardServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new AccountingServiceTestDbContext(options);
        _db.Database.EnsureCreated();

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

        _sut = new UnitDashboardService(_db, new AuditDescriber(), new AuditDiffBuilder());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetTimelineAsync_ScopesChildAuditRowsInSqlWithoutPreloadingIds()
    {
        var seeded = SeedUnitWithTimelineChildren();
        _db.AuditLogs.AddRange(
            Audit("Unit", seeded.Unit.Id, 6),
            Audit("Lease", seeded.Lease.Id, 5),
            Audit("Payment", seeded.Payment.Id, 4),
            Audit("WorkOrder", seeded.WorkOrder.Id, 3),
            Audit("Expense", seeded.Expense.Id, 2),
            Audit("Lease", seeded.ForeignLease.Id, 1));
        _db.SaveChanges();

        _executedSql.Clear();

        var rows = await _sut.GetTimelineAsync(PortfolioId, seeded.Unit.Id, skip: 0, take: 10, ct: CancellationToken.None);

        rows.Select(r => r.EntityType).Should().BeEquivalentTo(["Unit", "Lease", "Payment", "WorkOrder", "Expense"]);
        rows.Should().NotContain(r => r.EntityId == seeded.ForeignLease.Id);

        _executedSql.Should().NotContain(command => IsChildIdPreload(command),
            "unit timeline child scoping must stay inside the paged AuditLogs query instead of materializing child id lists");

        var auditSql = _executedSql.Single(command => command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase));
        auditSql.Should().Contain("ORDER BY", "timeline sorting must be DB-side");
        auditSql.Should().Contain("LIMIT", "timeline paging must be DB-side");
        auditSql.Should().Contain("Leases", "lease child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("Payments", "payment child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("WorkOrders", "work-order child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("Expenses", "expense child scope should be translated as a SQL subquery");
    }

    [Fact]
    public async Task GetDashboardAsync_IncludesExpenseDocumentsLinkedToTheUnitOrItsWorkOrders()
    {
        var seeded = SeedUnitWithTimelineChildren();
        var now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var directExpense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = seeded.Unit.PropertyId,
            UnitId = seeded.Unit.Id,
            Description = "Direct unit receipt",
            Amount = 55m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var foreignExpense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = seeded.ForeignLease.Unit!.PropertyId,
            UnitId = seeded.ForeignLease.UnitId,
            Description = "Other unit receipt",
            Amount = 75m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Expenses.AddRange(directExpense, foreignExpense);
        _db.SaveChanges();

        _db.StoredFiles.AddRange(
            File("Expense", seeded.Expense.Id, "work-order-receipt.pdf", now),
            File("Expense", directExpense.Id, "direct-unit-receipt.png", now.AddMinutes(-1)),
            File("Expense", foreignExpense.Id, "foreign-unit-receipt.pdf", now.AddMinutes(1)),
            File("Payment", seeded.Payment.Id, "rent-check.png", now.AddMinutes(-2)));
        _db.SaveChanges();

        _executedSql.Clear();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, seeded.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.Header.DocsNeedingReviewCount.Should().Be(3);
        dashboard.Overview.PendingDocs.Select(d => d.FileName)
            .Should().BeEquivalentTo(["work-order-receipt.pdf", "direct-unit-receipt.png", "rent-check.png"]);
        dashboard.Overview.PendingDocs.Should().NotContain(d => d.FileName == "foreign-unit-receipt.pdf");
        dashboard.Overview.PendingDocs.Count(d => d.EntityType == "Expense").Should().Be(2);

        _executedSql
            .Where(command => command.Contains("FROM \"StoredFiles\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(command => command.Contains("Expenses", StringComparison.OrdinalIgnoreCase),
                "unit document list/count queries must scope expense attachments in SQL, not after materialization");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksReadyNextActionToUnitApplicationLinkFlow()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Oak Ridge",
            AddressLine1 = "100 Oak",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "3B",
            Status = UnitStatus.Vacant,
            MarketRent = 975m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit);
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.Ready.ToString());
        dashboard.NextBestAction.Label.Should().Be("List this unit");
        dashboard.NextBestAction.Href.Should()
            .Be($"/applications?action=list-unit&propertyId={property.Id}&unitId={unit.Id}");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksRenewalNextActionToTenantRenewalNoticeFlow()
    {
        var now = DateTime.UtcNow;
        var (property, unit, tenant, _) = SeedRenewalUnit(now);

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.Renewal.ToString());
        dashboard.NextBestAction.Label.Should().StartWith("Send renewal");
        dashboard.NextBestAction.Href.Should()
            .Be($"/tenants/{tenant.Id}?action=create-notice&noticeType=RenewalOffer");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksApprovedRenewalNoticeToConversation()
    {
        var now = DateTime.UtcNow;
        var (property, unit, tenant, lease) = SeedRenewalUnit(now);
        var conversation = new Conversation
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Subject = "Lease renewal for Oak Ridge 3B",
            Property = property,
            StartedByLandlord = true,
            CreatedAt = now.AddMinutes(-2),
            LastMessageAt = now.AddMinutes(-2),
            LastMessagePreview = "Renewal offer",
            TenantUnreadCount = 1,
        };
        _db.Conversations.Add(conversation);
        _db.SaveChanges();

        _db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            Tenant = tenant,
            Property = property,
            NoticeType = "RenewalOffer",
            Status = "Approved",
            Subject = conversation.Subject,
            Body = "Please review your renewal offer.",
            Reason = "Lease ending soon",
            TriggerDate = now.Date,
            Conversation = conversation,
            ApprovedChannels = "Portal",
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-2),
            ApprovedAt = now.AddMinutes(-2),
        });
        _db.SaveChanges();
        _executedSql.Clear();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.Renewal.ToString());
        dashboard.NextBestAction.Label.Should().Be("Renewal sent - open conversation");
        dashboard.NextBestAction.Href.Should().Be($"/messages?conversation={conversation.Id}");

        var noticeSql = _executedSql.Single(command =>
            command.Contains("FROM \"NoticeDrafts\"", StringComparison.OrdinalIgnoreCase));
        noticeSql.Should().Contain("ORDER BY", "the renewal notice lookup must choose the latest relevant row in SQL");
        noticeSql.Should().Contain("LIMIT", "the renewal notice lookup must not materialize all matching notices");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksDraftRenewalNoticeToReviewFlow()
    {
        var now = DateTime.UtcNow;
        var (_, unit, tenant, lease) = SeedRenewalUnit(now);
        _db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            Tenant = tenant,
            NoticeType = "RenewalOffer",
            Status = "Draft",
            Subject = "Lease renewal",
            Body = "Draft renewal offer",
            Reason = "Lease ending soon",
            TriggerDate = now.Date,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
        });
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.Renewal.ToString());
        dashboard.NextBestAction.Label.Should().Be("Review renewal draft");
        dashboard.NextBestAction.Href.Should()
            .Be($"/tenants/{tenant.Id}?action=create-notice&noticeType=RenewalOffer");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksMoveInNextActionToConcreteWorkflow()
    {
        var now = DateTime.UtcNow;
        var (_, unit, _, _) = SeedMoveInUnit(now);

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.MoveIn.ToString());
        dashboard.NextBestAction.Label.Should().Be("Confirm move-in / collect deposit");
        dashboard.NextBestAction.Href.Should()
            .Be($"/units/{unit.Id}?tab=lease&action=confirm-move-in");
    }

    [Fact]
    public async Task GetDashboardAsync_RecentMoveInWithHeldDepositResolvesToActive()
    {
        var now = DateTime.UtcNow;
        var (_, unit, _, lease) = SeedMoveInUnit(now);
        _db.SecurityDepositHoldings.Add(new SecurityDepositHolding
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            Amount = lease.SecurityDeposit,
            Status = SecurityDepositStatus.Held,
            HeldAt = now,
            DeductionsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be(UnitLifecycleStage.Active.ToString());
        dashboard.NextBestAction.Label.Should().Be("Rent on track");
    }

    [Fact]
    public async Task GetDashboardAsync_PartialPaymentContributesOnlyItsRemainderToOutstanding()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Birch Lane",
            AddressLine1 = "300 Birch",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "101",
            Status = UnitStatus.Occupied,
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Quincy",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-101",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // One rent paid in full, one rent partially paid ($700 collected of $1,000 → $300 still owed).
        var paid = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1000m,
            DueDate = now.AddDays(-30),
            PaidDate = now.AddDays(-30),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var partial = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Partial,
            Amount = 1000m,
            AmountPaid = 700m,
            DueDate = now.AddDays(-1),
            PaidDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant, lease, paid, partial);
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        // The Paid rent contributes $0 and the Partial contributes only its $300 remainder (not its full
        // $1,000) — so the Unit Rent tab reconciles with the lease ledger Balance and the Accounting
        // Outstanding KPI instead of over-counting the already-collected $700.
        dashboard!.Header.OutstandingRentBalance.Should().Be(300m);
    }

    private static bool IsChildIdPreload(string command)
        => IsBareIdSelect(command, "Leases")
            || IsBareIdSelect(command, "Payments")
            || IsBareIdSelect(command, "WorkOrders")
            || IsBareIdSelect(command, "Inspections")
            || IsBareIdSelect(command, "Appointments")
            || IsBareIdSelect(command, "Expenses");

    private static bool IsBareIdSelect(string command, string table)
        => command.Contains($"SELECT \"", StringComparison.OrdinalIgnoreCase)
            && command.Contains($"\".\"Id\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains($"FROM \"{table}\"", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase);

    private AuditLog Audit(string entityType, int entityId, int minutesAgo)
        => new()
        {
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = AuditLogOperation.Created,
            ActorLabel = "test",
            Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
        };

    private static StoredFile File(string entityType, int entityId, string fileName, DateTime uploadedAt)
        => new()
        {
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            FileName = fileName,
            FilePath = fileName,
            ContentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/pdf",
            FileSize = 1024,
            UploadedAt = uploadedAt,
        };

    private (Property Property, Unit Unit, Tenant Tenant, Lease Lease) SeedRenewalUnit(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Oak Ridge",
            AddressLine1 = "100 Oak",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "3B",
            Status = UnitStatus.Occupied,
            MarketRent = 975m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Riley",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-3B",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-10),
            EndDate = now.AddDays(44),
            MonthlyRent = 975m,
            SecurityDeposit = 975m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant, lease);
        _db.SaveChanges();

        return (property, unit, tenant, lease);
    }

    private (Property Property, Unit Unit, Tenant Tenant, Lease Lease) SeedMoveInUnit(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Heights",
            AddressLine1 = "200 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "2A",
            Status = UnitStatus.Occupied,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Morgan",
            LastName = "Movein",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-MOVEIN",
            Status = LeaseStatus.Active,
            StartDate = now.AddDays(-5),
            EndDate = now.AddMonths(12),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant, lease);
        _db.SaveChanges();

        return (property, unit, tenant, lease);
    }

    private SeededTimelineGraph SeedUnitWithTimelineChildren()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-1A",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = now.Date,
            PaidDate = now.Date,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Fix sink",
            Description = "Leak",
            RequestedAt = now,
            UpdatedAt = now,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            WorkOrder = workOrder,
            Description = "Parts",
            Amount = 40m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var foreignUnit = new Unit
        {
            Property = property,
            UnitNumber = "9Z",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var foreignLease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = foreignUnit,
            Tenant = tenant,
            LeaseNumber = "L-9Z",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 900m,
            SecurityDeposit = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.AddRange(property, unit, tenant, lease, payment, workOrder, expense, foreignUnit, foreignLease);
        _db.SaveChanges();

        return new SeededTimelineGraph(unit, lease, payment, workOrder, expense, foreignLease);
    }

    private sealed record SeededTimelineGraph(
        Unit Unit,
        Lease Lease,
        Payment Payment,
        WorkOrder WorkOrder,
        Expense Expense,
        Lease ForeignLease);
}
