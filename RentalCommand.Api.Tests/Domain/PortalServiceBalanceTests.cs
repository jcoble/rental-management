using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class PortalServiceBalanceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly PortalService _sut;

    public PortalServiceBalanceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _ctx.Db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_tenant_account_balances" AS
            SELECT account."PortfolioId" AS "PortfolioId",
                   account."LeaseManagementId" AS "LeaseManagementId",
                   account."Id" AS "TenantAccountId",
                   max(
                     COALESCE((
                       SELECT sum(entry."Amount")
                       FROM "TenantLedgerEntries" AS entry
                       WHERE entry."PortfolioId" = account."PortfolioId"
                         AND entry."TenantAccountId" = account."Id"
                         AND entry."Direction" = 'Debit'
                     ), 0) - COALESCE((
                       SELECT sum(entry."Amount")
                       FROM "TenantLedgerEntries" AS entry
                       WHERE entry."PortfolioId" = account."PortfolioId"
                         AND entry."TenantAccountId" = account."Id"
                         AND entry."Direction" = 'Credit'
                     ), 0),
                     0
                   ) AS "ReceivableBalance",
                   COALESCE((
                     SELECT sum(max(charge."Amount" - COALESCE((
                       SELECT sum(allocation."Amount")
                       FROM "TenantLedgerAllocations" AS allocation
                       WHERE allocation."PortfolioId" = charge."PortfolioId"
                         AND allocation."TenantAccountId" = charge."TenantAccountId"
                         AND allocation."DebitEntryId" = charge."Id"
                     ), 0), 0))
                     FROM "TenantLedgerEntries" AS charge
                     WHERE charge."PortfolioId" = account."PortfolioId"
                       AND charge."TenantAccountId" = account."Id"
                       AND charge."Direction" = 'Debit'
                       AND charge."DueOn" < date('now')
                   ), 0) AS "PastDueAmount",
                   COALESCE((
                     SELECT count(*)
                     FROM "TenantLedgerEntries" AS charge
                     WHERE charge."PortfolioId" = account."PortfolioId"
                       AND charge."TenantAccountId" = account."Id"
                       AND charge."Direction" = 'Debit'
                       AND charge."DueOn" < date('now')
                       AND charge."Amount" > COALESCE((
                         SELECT sum(allocation."Amount")
                         FROM "TenantLedgerAllocations" AS allocation
                         WHERE allocation."PortfolioId" = charge."PortfolioId"
                           AND allocation."TenantAccountId" = charge."TenantAccountId"
                           AND allocation."DebitEntryId" = charge."Id"
                       ), 0)
                   ), 0) AS "PastDueCount"
            FROM "TenantAccounts" AS account
            """);
        _sut = new PortalService(_ctx.Db, new NoopLeaseQaService(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetBalanceAsync_TreatsDueTodayAsOutstandingButNotOverdue()
    {
        var tenant = SeedTenant("Blake", "Hayes");
        var otherTenant = SeedTenant("Other", "Tenant");
        var account = SeedTenantAccount(tenant);
        var otherAccount = SeedTenantAccount(otherTenant);
        var todayUtc = DateTime.UtcNow.Date;

        SeedCharge(account, 100m, todayUtc);
        SeedCharge(account, 80m, todayUtc, amountPaid: 20m);
        SeedCharge(account, 30m, todayUtc.AddDays(-1));
        SeedCharge(account, 40m, todayUtc.AddDays(-1));
        SeedCharge(account, 25m, todayUtc, amountPaid: 25m);
        SeedCharge(otherAccount, 999m, todayUtc.AddDays(-1));
        _ctx.Db.SaveChanges();

        _commands.Clear();

        var result = await _sut.GetBalanceAsync(PortfolioId, tenant.Id);

        result.Collected.Should().Be(45m);
        result.Outstanding.Should().Be(230m);
        result.Overdue.Should().Be(70m);
        result.OverdueCount.Should().Be(2);

        var balanceQueries = _commands
            .Where(sql => sql.Contains("vw_tenant_account_balances", StringComparison.OrdinalIgnoreCase))
            .ToList();
        balanceQueries.Should().ContainSingle("portal balance should be one DB-side aggregate query");
        balanceQueries[0].Should().Contain("ef_sum");
        balanceQueries[0].Should().Contain("COUNT");
    }

    [Fact]
    public async Task GetBalanceAsync_CountsUnsettledPastDueChargeAsOutstandingAndOverdue()
    {
        // One old charge is fully settled while a newer past-due charge remains entirely open.
        // Collected cash and outstanding receivables therefore both equal one month's rent.
        var tenant = SeedTenant("Marcus", "Williams");
        var account = SeedTenantAccount(tenant);
        var todayUtc = DateTime.UtcNow.Date;

        SeedCharge(account, 1050m, todayUtc.AddDays(-1));
        SeedCharge(account, 1050m, todayUtc.AddDays(-30), amountPaid: 1050m);
        _ctx.Db.SaveChanges();

        _commands.Clear();

        var result = await _sut.GetBalanceAsync(PortfolioId, tenant.Id);

        result.Collected.Should().Be(1050m);
        result.Outstanding.Should().Be(1050m);
        result.Overdue.Should().Be(1050m);
        result.OverdueCount.Should().Be(1);

        var balanceQueries = _commands
            .Where(sql => sql.Contains("vw_tenant_account_balances", StringComparison.OrdinalIgnoreCase))
            .ToList();
        balanceQueries.Should().ContainSingle("portal balance should remain one DB-side aggregate query");
    }

    private Tenant SeedTenant(string firstName, string lastName)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private TenantAccountFixture SeedTenantAccount(Tenant tenant)
    {
        var now = DateTime.UtcNow;
        var actor = _ctx.Db.Users.SingleOrDefault(user => user.Id == 1);
        if (actor is null)
        {
            actor = new ApplicationUser
            {
                Id = 1,
                UserName = "portal-balance@example.test",
                NormalizedUserName = "PORTAL-BALANCE@EXAMPLE.TEST",
                Email = "portal-balance@example.test",
                NormalizedEmail = "PORTAL-BALANCE@EXAMPLE.TEST",
                DisplayName = "Portal Balance Actor",
                CreatedAt = now,
            };
            _ctx.Db.Users.Add(actor);
        }
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"{tenant.FirstName} Flats",
            AddressLine1 = "1188 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43201",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "2B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(property, unit);
        _ctx.Db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{tenant.Id}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(management);
        _ctx.Db.SaveChanges();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{tenant.Id}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{tenant.Id}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 1200m,
            RentDueDay = 1,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 0m,
            GracePeriodDays = 0,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, actor.Id, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Canonical portal balance fixture",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        });
        _ctx.Db.AddRange(account, agreement);
        _ctx.Db.SaveChanges();
        return new TenantAccountFixture(account, agreement);
    }

    private void SeedCharge(
        TenantAccountFixture fixture,
        decimal amount,
        DateTime dueDate,
        decimal? amountPaid = null)
    {
        var now = DateTime.UtcNow;
        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = fixture.Account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueDate),
            DueOn = DateOnly.FromDateTime(dueDate),
            PostedAtUtc = now,
            Description = "Rent charge",
            BusinessKey = $"portal-charge:{Guid.NewGuid():N}",
            LeaseAgreementId = fixture.Agreement.Id,
            CreatedByUserId = 1,
        };
        _ctx.Db.TenantLedgerEntries.Add(charge);

        if (amountPaid is not > 0m)
            return;

        var receipt = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = fixture.Account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amountPaid.Value,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueDate),
            PostedAtUtc = now,
            Description = "Payment receipt",
            BusinessKey = $"portal-receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        _ctx.Db.TenantLedgerEntries.Add(receipt);
        _ctx.Db.SaveChanges();
        _ctx.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = fixture.Account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amountPaid.Value,
            AllocatedAtUtc = now,
            BusinessKey = $"portal-allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
    }

    private sealed record TenantAccountFixture(TenantAccount Account, LeaseAgreement Agreement);

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
    }
}
