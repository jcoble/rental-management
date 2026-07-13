using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// The load-bearing proof for the audit free-text search rewrite: <see
/// cref="AuditQueryService.ApplySearch"/> matches the visible audit-row fields with Postgres
/// <c>ILIKE</c> over the trgm-backed (<c>EntityType</c>/<c>ActorLabel</c>/<c>IpAddress</c>) columns
/// plus integer id-equality. SQLite has no <c>ILIKE</c> operator, so the case-insensitive / IP-substring
/// behavior can only be verified against a real Postgres — the SQLite unit twin asserted the same fields
/// but under a different operator (<c>LIKE</c>/no-op), which is why this lives here.
///
/// <para>Spins its OWN Postgres (Testcontainers, same pattern as <see cref="RlsTenantIsolationTests"/>),
/// creates the current model as the owner, seeds three
/// audit rows under one portfolio, and asserts the search hits. Queries run on the owner connection
/// (RLS bypassed); the service still scopes <c>WHERE PortfolioId == portfolioId</c>, which is what these
/// tests rely on.</para>
///
/// <para>Requires Docker. When Docker is unavailable the container fails to start and the test is
/// reported as <b>skipped</b> (via <c>[SkippableFact]</c> + <c>Skip.IfNot</c>) rather than failing.</para>
/// </summary>
public sealed class AuditSearchTests : IAsyncLifetime
{
    // Built inside InitializeAsync (not as a field initializer): PostgreSqlBuilder.Build() validates
    // the Docker endpoint eagerly, so building it here lets a missing daemon be caught and skipped.
    private PostgreSqlContainer? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;
    private int _portfolioId;
    private WorkspaceReadScope _scope;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            // No Docker daemon (or image pull/build-validation failed) — tests report as skipped.
            _dockerAvailable = false;
            return;
        }

        _ownerConnString = _pg.GetConnectionString();

        // This test proves the current EF projection and PostgreSQL ILIKE behavior independently of
        // migration-history correctness. The destructive InitialCreate migration has its own
        // apply/down/reapply proof and is regenerated only after the source model is final.
        await using var ctx = NewContext(_ownerConnString);
        await ctx.Database.EnsureCreatedAsync();

        // One portfolio (AuditLog.PortfolioId is a required FK → Portfolio), captured by id.
        var portfolio = new Portfolio
        {
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = "Authorized property",
            AddressLine1 = "1 Test Street",
            City = "Akron",
            State = "OH",
            PostalCode = "44308",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = _portfolioId,
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1_000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var expense = new Expense
        {
            Id = 76,
            PortfolioId = _portfolioId,
            Property = property,
            Unit = unit,
            Description = "Audit search expense",
            Amount = 25m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrder = new WorkOrder
        {
            Id = 11,
            PortfolioId = _portfolioId,
            Property = property,
            Unit = unit,
            Title = "Audit search work",
            Description = "Audit search work",
            RequestedAt = now,
            UpdatedAt = now,
        };
        var application = new RentalApplication
        {
            Id = 7,
            PortfolioId = _portfolioId,
            Property = property,
            Unit = unit,
            FirstName = "Jamie",
            LastName = "Applicant",
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.AddRange(property, unit, expense, workOrder, application);

        var user = new ApplicationUser
        {
            UserName = "audit-search@example.test",
            NormalizedUserName = "AUDIT-SEARCH@EXAMPLE.TEST",
            Email = "audit-search@example.test",
            NormalizedEmail = "AUDIT-SEARCH@EXAMPLE.TEST",
            DisplayName = "Audit Search Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = _portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        ctx.AddRange(assignment, session);
        await ctx.SaveChangesAsync();
        _scope = new WorkspaceReadScope(
            _portfolioId, user.Id, session.Id, context.Id, context.AccessRevision);

        // Three audit rows whose EntityType / EntityId / Operation / ActorLabel / IpAddress we control,
        // so the search assertions target exactly the fields the audit page renders. No users needed
        // (each row carries its own ActorLabel, so no UserId FK to satisfy).
        ctx.AuditLogs.AddRange(
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = "Expense", EntityId = 76,
                Operation = AuditLogOperation.Created, ActorLabel = "Jane Landlord",
                IpAddress = "203.0.113.5", Timestamp = DateTime.UtcNow.AddMinutes(-3),
            },
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = "WorkOrder", EntityId = 11,
                Operation = AuditLogOperation.Updated, ActorLabel = "Bob Staff",
                IpAddress = "198.51.100.9", Timestamp = DateTime.UtcNow.AddMinutes(-2),
            },
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = nameof(RentalApplication), EntityId = 7,
                Operation = AuditLogOperation.Created, ActorLabel = "Jane Landlord",
                IpAddress = "203.0.113.5", Timestamp = DateTime.UtcNow.AddMinutes(-1),
            });
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Search_Matches_Visible_Fields_On_Postgres()
    {
        SkipIfNoDocker();

        await using var db = NewContext(_ownerConnString);
        var sut = new AuditQueryService(
            db, new AuditDescriber(), new AuditDiffBuilder(), new FakeTimeZoneProvider(), TimeProvider.System);

        async Task<List<int>> SearchIds(string term)
        {
            var page = await sut.ListAsync(_scope, null, null, null, new ListQuery { Search = term });
            return page.Select(e => e.EntityId).ToList();
        }

        // Bare numeric "76" → matches the Expense #76 row by integer id equality.
        (await SearchIds("76")).Should().Equal(76);
        // Compound entity label exactly as rendered → parsed id.
        (await SearchIds("Expense #76")).Should().Equal(76);
        (await SearchIds("WorkOrder 11")).Should().Equal(11);
        // Entity type via ILIKE.
        (await SearchIds("application")).Should().Equal(7);
        // Actor label via ILIKE (lowercase term → case-insensitive match on "Bob Staff").
        (await SearchIds("bob")).Should().Equal(11);
        // IP address via ILIKE substring → both Jane rows share 203.0.113.5.
        (await SearchIds("203.0.113")).Should().BeEquivalentTo(new[] { 76, 7 });
        // Action verb → operation. "updated" narrows to the WorkOrder row.
        (await SearchIds("updated")).Should().Equal(11);
        // Friendly verbs map to the stored Created operation, so both Created rows match.
        (await SearchIds("recorded")).Should().BeEquivalentTo(new[] { 76, 7 });
        (await SearchIds("received")).Should().BeEquivalentTo(new[] { 76, 7 });
    }

    [SkippableFact]
    public async Task Page_Projection_Is_One_Translated_Paged_Command_With_All_Unit_Context_Sources()
    {
        SkipIfNoDocker();

        var commands = new ReaderCommandRecorder();
        await using var db = NewContext(_ownerConnString, commands);
        var sut = new AuditQueryService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            new FakeTimeZoneProvider(),
            TimeProvider.System);
        var query = new ListQuery { Skip = 1, Take = 2, Sort = "timestamp" };

        // ToQueryString proves that filtering, stable ordering, paging, actor lookup, and every
        // supported entity-to-Unit lookup form one provider-translated statement. It does not execute
        // the query, so the command recorder below remains an independent runtime query-count proof.
        var sql = sut.BuildPageProjectionQuery(_scope, null, null, null, query)
            .ToQueryString();

        sql.Should().Contain("FROM \"AuditLogs\"");
        sql.Should().Contain("\"AspNetUsers\"");
        sql.Should().Contain("\"WorkspaceAccessContexts\"");
        sql.Should().Contain("\"AuthSessions\"");
        sql.Should().Contain("reports.read");
        sql.Should().Contain("\"LeaseManagements\"");
        sql.Should().Contain("\"LeaseAgreements\"");
        sql.Should().Contain("\"TenantAccounts\"");
        sql.Should().Contain("\"WorkOrders\"");
        sql.Should().Contain("\"Expenses\"");
        sql.Should().Contain("\"RentalApplications\"");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");

        var page = await sut.ListAsync(_scope, null, null, null, query);

        page.Should().HaveCount(2);
        commands.ReaderCommands.Should().ContainSingle(
            "the audit page and all actor/Unit enrichment must execute as one SQL reader command");
        commands.ReaderCommands[0].Should().Contain("FROM \"AuditLogs\"");
    }

    // ───────────────────────────────── helpers ─────────────────────────────────

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; audit-search runtime verification skipped.");

    private static RentalCommandDbContext NewContext(
        string connString,
        params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connString)
            .AddInterceptors(interceptors)
            .Options);

    private sealed class ReaderCommandRecorder : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeTimeZoneProvider : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Utc;
    }
}
