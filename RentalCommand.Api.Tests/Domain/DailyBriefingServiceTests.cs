using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class DailyBriefingServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly DailyBriefingService _sut;

    public DailyBriefingServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new DailyBriefingFixtureDbContext(options);
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
        _db.Users.Add(new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "briefing-test@rentalcommand.local",
            NormalizedUserName = "BRIEFING-TEST@RENTALCOMMAND.LOCAL",
            Email = "briefing-test@rentalcommand.local",
            NormalizedEmail = "BRIEFING-TEST@RENTALCOMMAND.LOCAL",
            DisplayName = "Briefing Test Actor",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new DailyBriefingService(_db, new NoopLlmProvider(), TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ComposeAsync_RanksAndCapsBriefingCandidatesInSql()
    {
        SeedBriefingData();
        _executedSql.Clear();

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        briefing.Bullets.Should().Contain(b => b.Category == "Maintenance" && b.Severity == "critical");
        briefing.Bullets.Should().Contain(b => b.Category == "RentLate");
        briefing.Bullets.Should().Contain(b => b.Category == "RentDue");
        briefing.Bullets.Should().Contain(b => b.Category == "Appointment");
        briefing.Bullets.Should().Contain(b => b.Category == "Inspection");
        briefing.Bullets.Should().Contain(b => b.Category == "LeaseExpiring");
        briefing.Bullets
            .Where(b => b.EntityType is "WorkOrder" or nameof(TenantAccount) or nameof(LeaseAgreement))
            .Should().OnlyContain(b => b.UnitId > 0,
                "unit-tied dashboard action items should deep-link into the unit Command Center");

        _executedSql.Should().Contain(command =>
            command.Contains("UNION", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && command.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase)
            && command.Contains("DailyBriefingTestChargeBalances", StringComparison.OrdinalIgnoreCase)
            && command.Contains("DailyBriefingTestLeaseLifecycle", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LeaseAgreements", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Appointments", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Inspections", StringComparison.OrdinalIgnoreCase),
            "daily briefing candidate ranking and cap must happen in one DB-side query before bullet formatting");
    }

    [Fact]
    public async Task ComposeAsync_ExcludesEndedFixedTermLeasePaymentsFromActiveAttention()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var endedRelationship = SeedRelationship(
            today,
            leaseNumber: "L-OLD",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-15),
            endDate: today.AddMonths(-3),
            lifecycle: "Closed");
        var currentRelationship = SeedRelationship(
            today,
            leaseNumber: "L-CURRENT",
            propertyName: "Eastland 8-Plex",
            unitNumber: "4B",
            tenantFirstName: "Kevin",
            tenantLastName: "Brown",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6),
            lifecycle: "Occupied");

        SeedOpenRentCharge(endedRelationship, 925m, DateOnly.FromDateTime(today.AddMonths(-10)));
        SeedOpenRentCharge(currentRelationship, 975m, DateOnly.FromDateTime(today.AddDays(-7)));

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        briefing.Bullets.Should().ContainSingle(b =>
            b.Category == "RentLate" &&
            b.EntityId == currentRelationship.Account.Id);
        briefing.Bullets.Should().NotContain(b =>
            b.Category == "RentLate" &&
            b.Title.Contains("L-OLD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComposeAsync_RentAttentionUsesTenantUnitPropertyLabelInsteadOfLeaseNumber()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var relationship = SeedRelationship(
            today,
            leaseNumber: "L2024-003",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6),
            lifecycle: "Occupied");

        SeedOpenRentCharge(relationship, 925m, DateOnly.FromDateTime(today.AddDays(-7)));

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        var rentBullet = briefing.Bullets.Should().ContainSingle(b => b.Category == "RentLate").Subject;
        rentBullet.Title.Should().Contain("Jordan Smith");
        rentBullet.Title.Should().Contain("Westview Four-Plex");
        rentBullet.Title.Should().Contain("Unit 101");
        rentBullet.Title.Should().NotContain("L2024-003");
    }

    private void SeedBriefingData()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var relationship = SeedRelationship(
            today,
            leaseNumber: "L-1A",
            propertyName: "Maple",
            unitNumber: "1A",
            tenantFirstName: "Maria",
            tenantLastName: "Tenant",
            startDate: today.AddMonths(-1),
            endDate: today.AddDays(30),
            lifecycle: "Occupied");

        _db.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Title = "Water leak",
                Description = "Water is entering the kitchen ceiling.",
                Priority = WorkOrderPriority.Emergency,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                UpdatedAt = now,
            },
            new Appointment
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Title = "Showing",
                Type = AppointmentType.Showing,
                Status = AppointmentStatus.Scheduled,
                ScheduledStart = today.AddHours(14),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Inspection
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Type = InspectionType.Routine,
                Status = InspectionStatus.Scheduled,
                ScheduledFor = today.AddDays(1).AddHours(9),
                CreatedAt = now,
                UpdatedAt = now,
            });
        _db.SaveChanges();
        SeedOpenRentCharge(relationship, 1_200m, DateOnly.FromDateTime(today.AddDays(-6)));
        SeedOpenRentCharge(relationship, 1_200m, DateOnly.FromDateTime(today));
    }

    private CanonicalRelationship SeedRelationship(
        DateTime today,
        string leaseNumber,
        string propertyName,
        string unitNumber,
        string tenantFirstName,
        string tenantLastName,
        DateTime startDate,
        DateTime endDate,
        string lifecycle)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
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
            UnitNumber = unitNumber,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = tenantFirstName,
            LastName = tenantLastName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant);
        _db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"REL-{Guid.NewGuid():N}"[..12],
            PlannedPossessionAtUtc = startDate,
            PossessionGivenAtUtc = startDate,
            PossessionReturnedAtUtc = lifecycle == "Closed" ? endDate : null,
            AccountClosedAtUtc = lifecycle == "Closed" ? endDate : null,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var startOn = DateOnly.FromDateTime(startDate);
        var endOn = DateOnly.FromDateTime(endDate);
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = leaseNumber,
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = startOn,
            TermEndOn = endOn,
            GoverningFromOn = startOn,
            BaseRentAmount = 1_200m,
            RentDueDay = checked((short)today.Day),
            SecurityDepositObligation = 1_200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            IssuedAtUtc = now,
            FullyExecutedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = startOn,
            EffectiveThrough = lifecycle == "Closed" ? endOn : null,
            ChangeReason = "Canonical daily briefing fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{Guid.NewGuid():N}"[..12],
            Currency = "USD",
            OpenedAtUtc = startDate,
            ClosedAtUtc = lifecycle == "Closed" ? endDate : null,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _db.AddRange(agreement, party, account);
        _db.SaveChanges();

        _db.LeaseManagementLifecycleProjections.Add(new LeaseManagementLifecycleProjection
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            LeaseManagementId = management.Id,
            EffectiveNowUtc = now,
            BusinessDate = DateOnly.FromDateTime(today),
            Lifecycle = lifecycle,
            CurrentAgreementId = lifecycle == "Closed" ? null : agreement.Id,
            CurrentPartyCount = lifecycle == "Closed" ? 0 : 1,
            CurrentResidentCount = lifecycle == "Closed" ? 0 : 1,
            CurrentFinanciallyResponsiblePartyCount = lifecycle == "Closed" ? 0 : 1,
            CurrentPrimaryPartyId = lifecycle == "Closed" ? null : party.Id,
            CurrentPrimaryTenantId = lifecycle == "Closed" ? null : tenant.Id,
            CurrentPrimaryTenantName = lifecycle == "Closed" ? null : $"{tenantFirstName} {tenantLastName}",
            TenantAccountId = account.Id,
        });
        _db.LeaseAgreementStatusProjections.Add(new LeaseAgreementStatusProjection
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AgreementId = agreement.Id,
            BusinessDate = DateOnly.FromDateTime(today),
            GoverningFromOn = startOn,
            GoverningThroughExclusiveOn = endOn.AddDays(1),
            AgreementStatus = lifecycle == "Closed" ? "Expired" : "Active",
            IsGoverning = lifecycle != "Closed",
        });
        _db.SaveChanges();
        return new CanonicalRelationship(property, unit, tenant, management, agreement, party, account);
    }

    private TenantLedgerEntry SeedOpenRentCharge(
        CanonicalRelationship relationship,
        decimal amount,
        DateOnly dueOn)
    {
        var now = DateTime.UtcNow;
        var entry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = relationship.Account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            PostedAtUtc = now,
            Description = "Rent charge",
            BusinessKey = $"briefing-rent:{relationship.Account.Id}:{dueOn:yyyy-MM-dd}",
            LeaseAgreementId = relationship.Agreement.Id,
            CreatedByUserId = ActorUserId,
        };
        _db.TenantLedgerEntries.Add(entry);
        _db.SaveChanges();
        _db.TenantChargeBalanceProjections.Add(new TenantChargeBalanceProjection
        {
            PortfolioId = PortfolioId,
            TenantAccountId = relationship.Account.Id,
            TenantLedgerEntryId = entry.Id,
            BusinessDate = DateOnly.FromDateTime(now),
            EntryType = nameof(TenantLedgerEntryType.RentCharge),
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            OriginalAmount = amount,
            OpenAmount = amount,
            IsPastDue = dueOn < DateOnly.FromDateTime(now),
        });
        _db.SaveChanges();
        return entry;
    }

    private sealed record CanonicalRelationship(
        Property Property,
        Unit Unit,
        Tenant Tenant,
        LeaseManagement Management,
        LeaseAgreement Agreement,
        LeaseManagementParty Party,
        TenantAccount Account);

    private sealed class NoopLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
            => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "test"));
    }

    private sealed class DailyBriefingFixtureDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<LeaseManagementLifecycleProjection>()
                .HasKey(row => new { row.PortfolioId, row.LeaseManagementId });
            modelBuilder.Entity<LeaseManagementLifecycleProjection>()
                .ToTable("DailyBriefingTestLeaseLifecycle");
            modelBuilder.Entity<LeaseAgreementStatusProjection>()
                .HasKey(row => new { row.PortfolioId, row.AgreementId });
            modelBuilder.Entity<LeaseAgreementStatusProjection>()
                .ToTable("DailyBriefingTestAgreementStatus");
            modelBuilder.Entity<TenantChargeBalanceProjection>()
                .HasKey(row => new { row.PortfolioId, row.TenantAccountId, row.TenantLedgerEntryId });
            modelBuilder.Entity<TenantChargeBalanceProjection>()
                .ToTable("DailyBriefingTestChargeBalances");
        }
    }
}
