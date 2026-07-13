using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the unified audit trail: the SaveChanges interceptor (create/update/soft-delete capture,
/// no self-audit, de-dupe with the explicit log), the humanizer, and the portfolio-scoped query.
/// Backed by SQLite in-memory with jsonb columns mapped to TEXT.
/// </summary>
public sealed class AuditTrailTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int OtherPortfolioId = 2;

    private readonly SqliteConnection _conn;
    private readonly AuditScope _scope;
    private readonly FakeActor _actor;
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceReadScope _readScope;
    private readonly int _propertyId;
    private readonly int _otherPropertyId;

    public AuditTrailTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        _scope = new AuditScope();
        _actor = new FakeActor { UserId = 7, ActorLabel = "Jane Landlord", IpAddress = "203.0.113.5" };
        var interceptor = new AuditSaveChangesInterceptor(_actor, _scope, TimeProvider.System);

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(interceptor)
            .Options;

        _db = new AuditTestDbContext(options);
        _db.Database.EnsureCreated();

        SeedPortfolio(PortfolioId);
        SeedPortfolio(OtherPortfolioId);

        // The actor's UserId is a FK to ApplicationUser; seed it so SQLite's FK check is satisfied.
        var actorUser = new ApplicationUser
        {
            Id = 7,
            UserName = "jane@example.test",
            NormalizedUserName = "JANE@EXAMPLE.TEST",
            Email = "jane@example.test",
            NormalizedEmail = "JANE@EXAMPLE.TEST",
            DisplayName = "Jane Landlord",
        };
        _db.Users.Add(actorUser);
        _db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            User = actorUser,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _readScope = _db.SeedAdministratorScope(PortfolioId, "audit-query");
        _propertyId = SeedProperty(PortfolioId, "Authorized property");
        _otherPropertyId = SeedProperty(OtherPortfolioId, "Foreign property");
        _db.AuditLogs.RemoveRange(_db.AuditLogs);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private void SeedPortfolio(int id)
    {
        _db.Portfolios.Add(new Portfolio
        {
            Id = id,
            Name = $"Portfolio {id}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private int SeedProperty(int portfolioId, string name)
    {
        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = name,
            AddressLine1 = "1 Test Street",
            City = "Akron",
            State = "OH",
            PostalCode = "44308",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property.Id;
    }

    private Expense NewExpense(int portfolioId = PortfolioId) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = portfolioId == PortfolioId ? _propertyId : _otherPropertyId,
        Category = ScheduleECategory.Repairs,
        Description = "Roof repair",
        Status = ExpenseStatus.Pending,
        Amount = 100m,
        IncurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Create_Records_A_Single_Created_AuditRow_With_Actor()
    {
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        var rows = await _db.AuditLogs.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        var row = rows[0];
        row.Operation.Should().Be(AuditLogOperation.Created);
        row.EntityType.Should().Be("Expense");
        row.EntityId.Should().Be(expense.Id);
        row.EntityId.Should().BeGreaterThan(0);
        row.PortfolioId.Should().Be(PortfolioId);
        row.UserId.Should().Be(7);
        row.ActorLabel.Should().Be("Jane Landlord");
        row.IpAddress.Should().Be("203.0.113.5");
        row.NewValues.Should().NotBeNullOrEmpty();
        row.OldValues.Should().BeNull();
    }

    [Fact]
    public async Task Update_Records_Updated_With_Only_Changed_Properties()
    {
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        expense.Amount = 250m;
        expense.Description = "Roof repair (revised)";
        await _db.SaveChangesAsync();

        var updated = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuditLogOperation.Updated)
            .SingleAsync();

        updated.EntityId.Should().Be(expense.Id);
        var oldVals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updated.OldValues!)!;
        var newVals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updated.NewValues!)!;
        oldVals.Should().ContainKey("Amount");
        oldVals.Should().ContainKey("Description");
        oldVals.Should().NotContainKey("Category"); // unchanged → excluded
        newVals["Amount"].GetDecimal().Should().Be(250m);
        oldVals["Amount"].GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task SoftDelete_Records_Deleted_Not_Updated()
    {
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        expense.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var ops = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == expense.Id)
            .Select(a => a.Operation)
            .ToListAsync();

        ops.Should().BeEquivalentTo(new[] { AuditLogOperation.Created, AuditLogOperation.Deleted });
        ops.Should().NotContain(AuditLogOperation.Updated);
    }

    [Fact]
    public async Task AuditLog_Itself_Is_Not_Audited()
    {
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        // Exactly one row from the create — the audit write did not recurse into another audit row.
        (await _db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PreClaimed_Key_Suppresses_The_Interceptor_Row()
    {
        // Insert (Created is recorded), then have the explicit path take ownership of the Updated key
        // BEFORE the entity's save — as an AuditTrailService.LogAsync run pre-save would — so the
        // interceptor's generic Updated twin is suppressed.
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        _scope.ResolveExplicit("Expense", expense.Id, AuditLogOperation.Updated)
            .Decision.Should().Be(AuditWrite.Insert);

        expense.Amount = 500m;
        await _db.SaveChangesAsync();

        var updatedRows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == expense.Id && a.Operation == AuditLogOperation.Updated)
            .ToListAsync();

        // The interceptor deferred to the (pre-claimed) explicit log — no generic twin written.
        updatedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_Log_After_Save_Enriches_The_Generic_Twin()
    {
        // The order-independent guarantee: a service that logs AFTER its entity's SaveChanges (the
        // common pattern) must still win. The interceptor writes a generic twin with only the changed
        // property; the explicit rich log then enriches THAT row in place — full snapshot + reason —
        // rather than being suppressed (the pre-fix behavior) or adding a duplicate.
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        expense.Amount = 250m;
        await _db.SaveChangesAsync(); // interceptor records a generic Updated twin: { Amount: 100 → 250 }

        var sut = new AuditTrailService(_db, _scope, TimeProvider.System);
        await sut.LogAsync(
            PortfolioId, "Expense", expense.Id, AuditLogOperation.Updated,
            oldValues: """{"Amount":100,"Status":"Pending"}""",
            newValues: """{"Amount":250,"Status":"Pending"}""",
            changeReason: "Roof repair re-quoted");

        var updated = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == expense.Id && a.Operation == AuditLogOperation.Updated)
            .ToListAsync();

        updated.Should().ContainSingle("the explicit log enriches the generic twin, it does not add a second row");
        updated[0].ChangeReason.Should().Be("Roof repair re-quoted");
        // The full explicit snapshot replaced the changed-property-only twin (Status was not modified,
        // so its presence proves enrichment happened).
        updated[0].NewValues.Should().Contain("Status");
        // Actor / IP from the generic capture survive (the explicit caller passed null for them).
        updated[0].ActorLabel.Should().Be("Jane Landlord");
        updated[0].IpAddress.Should().Be("203.0.113.5");
    }

    [Fact]
    public async Task Query_Is_Portfolio_Scoped_And_NewestFirst()
    {
        // Two expenses in this portfolio, one in another portfolio.
        var first = NewExpense();
        _db.Expenses.Add(first);
        await _db.SaveChangesAsync();

        var second = NewExpense();
        second.Description = "Plumbing";
        _db.Expenses.Add(second);
        await _db.SaveChangesAsync();

        var foreign = NewExpense(OtherPortfolioId);
        _db.Expenses.Add(foreign);
        await _db.SaveChangesAsync();

        var sut = NewAuditQueryService();
        var page = await sut.ListAsync(_readScope, null, null, null, new ListQuery());

        page.Should().OnlyContain(e => e.PortfolioId == PortfolioId);
        page.Select(e => e.EntityId).Should().NotContain(foreign.Id);
        // Newest-first: the second expense's audit row precedes the first's.
        page.First().EntityId.Should().Be(second.Id);
    }

    [Fact]
    public async Task Query_Filters_By_Operation_And_EntityType()
    {
        var expense = NewExpense();
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();
        expense.Amount = 999m;
        await _db.SaveChangesAsync();

        var sut = NewAuditQueryService();

        var created = await sut.ListAsync(_readScope, AuditLogOperation.Created, "Expense", null, new ListQuery());
        created.Should().ContainSingle();
        created[0].Operation.Should().Be(AuditLogOperation.Created);
        created[0].Description.Should().Be("Recorded an expense");
        created[0].DetailHref.Should().Be($"/accounting/expenses/{expense.Id}");

        var wrongType = await sut.ListAsync(_readScope, null, "Lease", null, new ListQuery());
        wrongType.Should().BeEmpty();
    }

    // NOTE: free-text audit search (AuditQueryService.ApplySearch) is covered by
    // RentalCommand.IntegrationTests/AuditSearchTests.cs — it uses Postgres ILIKE, which the
    // SQLite provider here cannot translate. That test runs against real Postgres (Testcontainers)
    // and self-skips without Docker.

    [Fact]
    public async Task Query_Resolves_Actor_DisplayName_When_Row_Has_Only_UserId()
    {
        // The reported "User #1" bug: a row written with a user id but no ActorLabel (e.g. an HTTP
        // request whose token lacked a name claim) must render the user's display name, not "User #7".
        // A user with a blank display name falls back to the email; a row with no user id stays "system".
        var noNameUser = new ApplicationUser
        {
            UserName = "noname@example.test",
            NormalizedUserName = "NONAME@EXAMPLE.TEST",
            Email = "noname@example.test",
            NormalizedEmail = "NONAME@EXAMPLE.TEST",
            DisplayName = "",
        };
        _db.Users.Add(noNameUser);
        _db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            User = noNameUser,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var units = new[]
        {
            Unit("71"),
            Unit("72"),
            Unit("73"),
        };
        _db.Units.AddRange(units);
        await _db.SaveChangesAsync();

        _db.AuditLogs.AddRange(
            new AuditLog { PortfolioId = PortfolioId, EntityType = nameof(Unit), EntityId = units[0].Id, Operation = AuditLogOperation.Created, UserId = 7, ActorLabel = null, Timestamp = DateTime.UtcNow.AddMinutes(-3) },
            new AuditLog { PortfolioId = PortfolioId, EntityType = nameof(Unit), EntityId = units[1].Id, Operation = AuditLogOperation.Created, UserId = noNameUser.Id, ActorLabel = null, Timestamp = DateTime.UtcNow.AddMinutes(-2) },
            new AuditLog { PortfolioId = PortfolioId, EntityType = nameof(Unit), EntityId = units[2].Id, Operation = AuditLogOperation.Created, UserId = null, ActorLabel = null, Timestamp = DateTime.UtcNow.AddMinutes(-1) });
        await _db.SaveChangesAsync();

        var sut = NewAuditQueryService();
        var page = await sut.ListAsync(_readScope, null, nameof(Unit), null, new ListQuery());

        // UserId present, no ActorLabel → resolved display name (NOT "User #7").
        page.Single(p => p.EntityId == units[0].Id).Actor.Should().Be("Jane Landlord");
        // Display name blank → falls back to email.
        page.Single(p => p.EntityId == units[1].Id).Actor.Should().Be("noname@example.test");
        // No user id at all → system.
        page.Single(p => p.EntityId == units[2].Id).Actor.Should().Be("system");
    }

    [Fact]
    public async Task Query_Applies_SelectedProperty_Scope_Before_Paging()
    {
        var decoyPropertyId = SeedProperty(PortfolioId, "Out-of-scope decoy");
        var authorized = NewExpense();
        var decoy = NewExpense();
        decoy.PropertyId = decoyPropertyId;
        decoy.Description = "Must not appear";
        _db.Expenses.AddRange(authorized, decoy);

        var assignment = _db.MembershipRoleAssignments
            .Include(item => item.SelectedProperties)
            .Single(item => item.WorkspaceMembership!.AccessContextId == _readScope.AccessContextId);
        assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PortfolioId = PortfolioId,
            PropertyId = _propertyId,
        });
        await _db.SaveChangesAsync();

        var page = await NewAuditQueryService().ListAsync(
            _readScope,
            AuditLogOperation.Created,
            nameof(Expense),
            null,
            new ListQuery { Take = 1 });

        page.Should().ContainSingle();
        page[0].EntityId.Should().Be(authorized.Id,
            "the unauthorized newer decoy must be removed before Take(1)");
    }

    [Fact]
    public async Task Query_FailsClosed_For_Stale_AccessRevision()
    {
        var page = await NewAuditQueryService().ListAsync(
            _readScope with { AccessRevision = _readScope.AccessRevision + 1 },
            null,
            null,
            null,
            new ListQuery());

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_FailsClosed_For_Revoked_Session()
    {
        var session = await _db.AuthSessions.SingleAsync(item => item.Id == _readScope.SessionId);
        session.RevokedAtUtc = DateTime.UtcNow;
        session.Status = AuthSessionStatus.Revoked;
        await _db.SaveChangesAsync();

        var page = await NewAuditQueryService().ListAsync(
            _readScope,
            null,
            null,
            null,
            new ListQuery());

        page.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(LeaseManagement), "/units/42?tab=lease&leaseManagement=81")]
    [InlineData(nameof(LeaseAgreement), "/units/42?tab=lease&agreement=81")]
    [InlineData(nameof(TenantAccount), "/units/42?tab=ledger&tenantAccount=81")]
    [InlineData(nameof(WorkOrder), "/units/42?tab=maintenance&wo=81")]
    [InlineData(nameof(Expense), "/units/42?tab=ledger&ledger=expenses&expense=81")]
    [InlineData(nameof(RentalApplication), "/units/42?tab=applications&app=81")]
    public void Query_Routes_Supported_Canonical_Entities_To_Unit_CommandCenter(
        string entityType,
        string expectedHref)
    {
        AuditEntryResponse.BuildDetailHref(entityType, 81, 42)
            .Should().Be(expectedHref);
    }

    [Theory]
    [InlineData("TenantLedgerEntry", AuditLogOperation.Created, "Posted a tenant account entry")]
    [InlineData("TenantAccount", AuditLogOperation.Updated, "Updated tenant account")]
    [InlineData("LeaseAgreement", AuditLogOperation.Updated, "Updated lease agreement")]
    [InlineData("WorkOrder", AuditLogOperation.Created, "Created a work order")]
    [InlineData("Expense", AuditLogOperation.Deleted, "Deleted expense")]
    [InlineData("Tenant", AuditLogOperation.Created, "Added tenant")]
    public void Describer_Produces_PlainEnglish(string entityType, AuditLogOperation op, string expected)
    {
        var describer = new AuditDescriber();
        var sentence = describer.Describe(new AuditLog { EntityType = entityType, EntityId = 5, Operation = op });
        sentence.Should().Be(expected);
    }

    private AuditQueryService NewAuditQueryService() =>
        new(_db, new AuditDescriber(), new AuditDiffBuilder(), new FakeTimeZoneProvider(), TimeProvider.System);

    private Unit Unit(string number) => new()
    {
        PortfolioId = PortfolioId,
        PropertyId = _propertyId,
        UnitNumber = number,
        MarketRent = 1_000m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class FakeActor : ICurrentActor
    {
        public int? UserId { get; init; }
        public string? ActorLabel { get; init; }
        public string? IpAddress { get; init; }
    }

    private sealed class FakeTimeZoneProvider : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }
}

/// <summary>
/// Derived DbContext that maps Postgres jsonb columns to TEXT and drops Postgres-only DDL so the
/// schema is valid under SQLite (mirrors the existing accounting test context).
/// </summary>
internal sealed class AuditTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public AuditTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
