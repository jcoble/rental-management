using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Import;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL boundary proof that CSV receipts preserve imported/unapplied intent through atomic
/// execution, exact replay, and companion-write rollback.
/// </summary>
public sealed class AtomicPaymentCsvImportPostgreSqlTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Model);
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OutboxFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<OutboxFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(UnitOccupancyViewSql.Create);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Import_PersistsOneUnappliedReceipt_PreservesOlderCharge_AndReplaysExactly()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("replay");
        var command = Command(scenario, "csv-import-replay");
        var identity = AtomicPaymentCsvImport.Identity(command);

        var first = await ExecuteAtomicAsync(identity, command);
        var replay = await ExecuteAtomicAsync(identity, command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.CreatedRows.Should().Be(1);

        await using var db = NewContext();
        var receipt = await db.TenantLedgerEntries.SingleAsync(entry =>
            entry.PortfolioId == scenario.PortfolioId
            && entry.EntryType == TenantLedgerEntryType.PaymentReceipt);
        var attempt = await db.TenantPaymentAttempts.SingleAsync(row =>
            row.PortfolioId == scenario.PortfolioId);
        attempt.AttemptType.Should().Be(TenantPaymentAttemptType.ImportedReceipt);
        attempt.ChargeLedgerEntryId.Should().BeNull();
        attempt.IdempotencyKey.Should().Be(scenario.DeliveryKey);
        receipt.ProviderPaymentAttemptId.Should().Be(attempt.Id);
        receipt.BusinessKey.Should().StartWith("csv-receipt:");
        (await db.TenantLedgerAllocations.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
        (await db.TenantLedgerEntries.CountAsync(entry =>
            entry.Id == scenario.OlderChargeEntryId
            && entry.Amount == scenario.Amount
            && entry.Direction == TenantLedgerDirection.Debit
            && entry.EntryType == TenantLedgerEntryType.ManualCharge)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task OutboxFailure_RollsBackReceiptAttemptAuditAndCommandReceipt_ThenAllowsExactRetry()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("rollback");
        var command = Command(scenario, "csv-import-rollback");
        var identity = AtomicPaymentCsvImport.Identity(command);
        Failure.FailNextOutboxInsert = true;

        var act = async () =>
            await ExecuteAtomicAsync(identity, command);
        await act.Should().ThrowAsync<DbUpdateException>();

        await using (var failed = NewContext())
        {
            (await failed.TenantPaymentAttempts.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
            (await failed.TenantLedgerEntries.CountAsync(entry =>
                entry.PortfolioId == scenario.PortfolioId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt)).Should().Be(0);
            (await failed.TenantLedgerAllocations.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(audit =>
                audit.CommandType == identity.CommandType
                && audit.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == identity.CommandType
                && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.OutboxMessages.CountAsync(message =>
                message.PortfolioId == scenario.PortfolioId)).Should().Be(0);
            (await failed.TenantLedgerEntries.CountAsync(entry =>
                entry.Id == scenario.OlderChargeEntryId
                && entry.Amount == scenario.Amount
                && entry.Direction == TenantLedgerDirection.Debit
                && entry.EntryType == TenantLedgerEntryType.ManualCharge)).Should().Be(1);
        }

        var recovered = await ExecuteAtomicAsync(
            identity,
            command);
        var replay = await ExecuteAtomicAsync(
            identity,
            command);

        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(recovered.Value);
        await using var recoveredDb = NewContext();
        (await recoveredDb.TenantPaymentAttempts.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.AttemptType == TenantPaymentAttemptType.ImportedReceipt
            && row.ChargeLedgerEntryId == null)).Should().Be(1);
        (await recoveredDb.TenantLedgerEntries.CountAsync(entry =>
            entry.PortfolioId == scenario.PortfolioId
            && entry.EntryType == TenantLedgerEntryType.PaymentReceipt)).Should().Be(1);
        (await recoveredDb.TenantLedgerAllocations.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
        (await recoveredDb.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"csv-import-{suffix}@example.test",
            NormalizedUserName = $"CSV-IMPORT-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"csv-import-{suffix}@example.test",
            NormalizedEmail = $"CSV-IMPORT-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"CSV import {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"CSV import {suffix}",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Property {suffix}",
            AddressLine1 = "1 Import Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, unit);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();

        db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "Import",
            LastName = suffix,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        db.AddRange(session, tenant, management);
        await db.SaveChangesAsync();

        db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "CSV payment import boundary proof",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        });
        var account = new TenantAccount
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.TenantAccounts.Add(account);
        await db.SaveChangesAsync();

        const decimal amount = 875m;
        var olderCharge = new TenantLedgerEntry
        {
            PortfolioId = portfolio.Id,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            DueOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            PostedAtUtc = now.AddMonths(-1),
            Description = "Older open charge",
            BusinessKey = $"test:csv-import:{suffix}:older-charge",
            CreatedByUserId = user.Id,
        };
        db.TenantLedgerEntries.Add(olderCharge);
        await db.SaveChangesAsync();

        return new Scenario(
            portfolio.Id,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            management.RelationshipNumber,
            olderCharge.Id,
            amount,
            DateOnly.FromDateTime(now),
            $"csv-import:{suffix}:delivery");
    }

    private static AtomicPaymentCsvImportCommand Command(Scenario scenario, string digest) => new(
        scenario.PortfolioId,
        scenario.UserId,
        scenario.AuthSessionId,
        scenario.AccessContextId,
        scenario.AccessRevision,
        digest,
        JsonSerializer.Serialize(
        new[]
        {
            new
            {
                RowNumber = 1,
                scenario.RelationshipNumber,
                PropertyName = "",
                UnitNumber = "",
                scenario.Amount,
                scenario.PaidOn,
                Method = "ACH",
                ExternalReference = $"ref-{digest}",
                Description = "Imported tenant payment",
                scenario.DeliveryKey,
                Errors = Array.Empty<string>(),
            },
        }));

    private async Task<AtomicCommandOutcome<AtomicPaymentCsvImportResult>> ExecuteAtomicAsync(
        AtomicCommandIdentity identity,
        AtomicPaymentCsvImportCommand command)
    {
        await using var scope = _services!.CreateAsyncScope();
        var handler = new AtomicPaymentCsvImportRule(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        return await scope.ServiceProvider
            .GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey,
                AtomicPaymentCsvImport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private OutboxFailureInterceptor Failure =>
        _services!.GetRequiredService<OutboxFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for atomic CSV payment import tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:csv-payment-import";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class OutboxFailureInterceptor : DbCommandInterceptor
    {
        public bool FailNextOutboxInsert { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedOutboxFailure : Exception
    {
    }

    private sealed record Scenario(
        int PortfolioId,
        int UserId,
        Guid AuthSessionId,
        int AccessContextId,
        long AccessRevision,
        string RelationshipNumber,
        long OlderChargeEntryId,
        decimal Amount,
        DateOnly PaidOn,
        string DeliveryKey);
}
