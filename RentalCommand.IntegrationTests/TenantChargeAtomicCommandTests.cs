using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Real-PostgreSQL proof for canonical manual tenant charges and typed charge reversals. The
/// foundation is still forward-only, so this suite uses EnsureCreated and installs the same
/// effective-clock function and charge-balance view that the destructive baseline will own.
/// </summary>
public sealed class TenantChargeAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<TenantChargeMutationResult> ChargeCodec =
        new("tenant-account.charge.mutation.v1");
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_tenant_charge_atomic")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CompanionFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            PostTenantChargeCommand,
            TenantChargeMutationResult,
            PostTenantChargeHandler>();
        services.AddAtomicCommandHandler<
            ReverseTenantChargeCommand,
            TenantChargeMutationResult,
            ReverseTenantChargeHandler>();
        services.AddAtomicCommandHandler<
            RecordTenantReceiptCommand,
            RecordTenantReceiptResult,
            RecordTenantReceiptHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<CompanionFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await CreatePhysicalTestSchemaAsync(db);
    }

    private static async Task CreatePhysicalTestSchemaAsync(RentalCommandDbContext db)
    {
        // The foundation migration chain is intentionally temporary and will disappear at the
        // final baseline squash. Build the EF-owned tables directly, then install only the
        // canonical SQL objects used by charge/receipt allocation and reversal proofs.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ManualCharge_PostsOneDebitWithAuditOutboxAndReceipt_AndReplaysOnce()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("post-replay");
        var command = PostCharge(scenario, "post-replay", 425.50m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.post", command.DeliveryIdempotencyKey);

        var first = await Atomic.ExecuteAsync(identity, command, ChargeCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, ChargeCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        await using var db = NewContext();
        var charge = await db.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == command.BusinessKey);
        charge.EntryType.Should().Be(TenantLedgerEntryType.ManualCharge);
        charge.Direction.Should().Be(TenantLedgerDirection.Debit);
        charge.Amount.Should().Be(425.50m);
        charge.Currency.Should().Be("USD");
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        (await db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.IdempotencyKey == outboxKey)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_ReauthorizesAndDeniesRevokedSessionOrStaleAccessRevision(bool staleRevision)
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync(staleRevision ? "stale-replay" : "revoked-replay");
        var command = PostCharge(scenario, "replay-auth", 75m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.post", command.DeliveryIdempotencyKey);
        await Atomic.ExecuteAsync(identity, command, ChargeCodec);

        await using (var mutate = NewContext())
        {
            if (staleRevision)
            {
                var context = await mutate.WorkspaceAccessContexts.SingleAsync(row =>
                    row.Id == scenario.AccessContextId);
                context.AdvanceRevision(scenario.AccessRevision);
            }
            else
            {
                var session = await mutate.AuthSessions.SingleAsync(row => row.Id == scenario.SessionId);
                session.Status = AuthSessionStatus.Revoked;
                session.RevokedAtUtc = DateTime.UtcNow;
                session.RevocationReason = "integration replay proof";
            }
            await mutate.SaveChangesAsync();
        }

        await FluentActions.Invoking(() => Atomic.ExecuteAsync(identity, command, ChargeCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == command.BusinessKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Reversal_RejectsMissingCrossAccountAndNonChargeTargets()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("invalid-reversals");
        var nonCharge = await SeedLedgerEntryAsync(
            scenario, scenario.AccountId, TenantLedgerEntryType.PaymentReceipt,
            TenantLedgerDirection.Credit, "non-charge-target", 40m);
        var crossAccount = await SeedLedgerEntryAsync(
            scenario, scenario.OtherAccountId, TenantLedgerEntryType.ManualCharge,
            TenantLedgerDirection.Debit, "cross-account-target", 40m, DateOnly.FromDateTime(DateTime.UtcNow));

        foreach (var (entryId, suffix) in new[]
        {
            (long.MaxValue, "missing"),
            (crossAccount, "cross-account"),
            (nonCharge, "non-charge"),
        })
        {
            var command = ReverseCharge(scenario, entryId, suffix);
            var identity = new AtomicCommandIdentity(
                "tenant-account.charge.reverse", command.DeliveryIdempotencyKey);
            await FluentActions.Invoking(() => Atomic.ExecuteAsync(identity, command, ChargeCodec))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.EntryType == TenantLedgerEntryType.Reversal)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AllocatedChargeReversal_CopiesTermsAndAppendsExactNegativeAllocationsAtomically()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("allocated-reversal");
        var chargeCommand = PostCharge(scenario, "allocated-charge", 300m);
        var charge = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(scenario, "allocated-receipt", 125m);
        await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);

        var reversalCommand = ReverseCharge(scenario, charge.Value.LedgerEntryId, "allocated");
        var reversalIdentity = new AtomicCommandIdentity(
            "tenant-account.charge.reverse", reversalCommand.DeliveryIdempotencyKey);
        Failures.Clear();
        var reversal = await Atomic.ExecuteAsync(reversalIdentity, reversalCommand, ChargeCodec);

        reversal.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var db = NewContext();
        var entries = await db.TenantLedgerEntries
            .Where(row => row.Id == charge.Value.LedgerEntryId
                || row.Id == reversal.Value.LedgerEntryId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        var original = entries.Single(row => row.Id == charge.Value.LedgerEntryId);
        var reversingEntry = entries.Single(row => row.Id == reversal.Value.LedgerEntryId);
        reversingEntry.EntryType.Should().Be(TenantLedgerEntryType.Reversal);
        reversingEntry.Direction.Should().Be(TenantLedgerDirection.Credit);
        reversingEntry.Amount.Should().Be(original.Amount);
        reversingEntry.Currency.Should().Be(original.Currency);
        reversingEntry.ReversesEntryId.Should().Be(original.Id);

        var allocations = await db.TenantLedgerAllocations
            .Where(row => row.TenantAccountId == scenario.AccountId
                && row.DebitEntryId == original.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        allocations.Should().HaveCount(2);
        var applied = allocations.Single(row => row.ReversesAllocationId == null);
        var reversed = allocations.Single(row => row.ReversesAllocationId != null);
        reversed.ReversesAllocationId.Should().Be(applied.Id);
        reversed.Amount.Should().Be(-applied.Amount);
        Failures.Commands.Should().ContainSingle(sql =>
            sql.Contains("INSERT INTO \"TenantLedgerAllocations\"", StringComparison.Ordinal)
            && sql.Contains("-source.\"Amount\"", StringComparison.Ordinal)
            && sql.Contains("NOT EXISTS", StringComparison.Ordinal));
        reversal.Value.Amount.Should().Be(original.Amount);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == reversalIdentity.CommandType
            && row.IdempotencyKey == reversalIdentity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task AllocatedChargeReversal_FinalCompanionFailureRollsBackEveryWrite()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("reversal-rollback");
        var chargeCommand = PostCharge(scenario, "rollback-charge", 220m);
        var charge = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(scenario, "rollback-receipt", 80m);
        await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);
        var reversalCommand = ReverseCharge(scenario, charge.Value.LedgerEntryId, "rollback");
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.reverse", reversalCommand.DeliveryIdempotencyKey);

        Failures.FailAtomicAudit = true;
        try
        {
            var failure = await FluentActions
                .Invoking(() => Atomic.ExecuteAsync(identity, reversalCommand, ChargeCodec))
                .Should().ThrowAsync<DbUpdateException>();
            failure.WithInnerException<InvalidOperationException>()
                .WithMessage("injected tenant-charge audit failure");
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.ReversesEntryId == charge.Value.LedgerEntryId)).Should().Be(0);
        var allocations = await db.TenantLedgerAllocations
            .Where(row => row.TenantAccountId == scenario.AccountId
                && row.DebitEntryId == charge.Value.LedgerEntryId)
            .ToListAsync();
        allocations.Should().ContainSingle(row => row.ReversesAllocationId == null && row.Amount == 80m);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"tenant-money-{suffix}@example.test",
            NormalizedUserName = $"TENANT-MONEY-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"tenant-money-{suffix}@example.test",
            NormalizedEmail = $"TENANT-MONEY-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Tenant money {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"Tenant money {suffix}",
            ManagementCompanyName = $"Tenant money {suffix}",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Charge property {suffix}",
            AddressLine1 = "1 Ledger Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var firstUnit = Unit(portfolio.Id, property, "1", now);
        var secondUnit = Unit(portfolio.Id, property, "2", now);
        db.AddRange(property, firstUnit, secondUnit);
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
        db.Add(membership);
        await db.SaveChangesAsync();

        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PropertyId = property.Id,
            PortfolioId = portfolio.Id,
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
        var firstRelationship = Relationship(portfolio.Id, property.Id, firstUnit.Id, user.Id, $"{suffix}-1", now);
        var secondRelationship = Relationship(portfolio.Id, property.Id, secondUnit.Id, user.Id, $"{suffix}-2", now);
        db.AddRange(assignment, session, firstRelationship, secondRelationship);
        await db.SaveChangesAsync();

        var firstAccount = Account(portfolio.Id, firstRelationship.Id, user.Id, $"{suffix}-1", now);
        var secondAccount = Account(portfolio.Id, secondRelationship.Id, user.Id, $"{suffix}-2", now);
        db.AddRange(firstAccount, secondAccount);
        await db.SaveChangesAsync();
        return new Scenario(portfolio.Id, property.Id, firstAccount.Id, secondAccount.Id,
            user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private static Unit Unit(int portfolioId, Property property, string number, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        Property = property,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagement Relationship(
        int portfolioId, int propertyId, int unitId, int userId, string suffix, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitId = unitId,
        RelationshipNumber = $"LM-{suffix}",
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static TenantAccount Account(
        int portfolioId, int relationshipId, int userId, string suffix, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        LeaseManagementId = relationshipId,
        AccountNumber = $"TA-{suffix}",
        Currency = "USD",
        OpenedAtUtc = now,
        CreatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static PostTenantChargeCommand PostCharge(Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            $"Manual charge {suffix}", null, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"manual-charge:{suffix}", $"tenant-charge:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static RecordTenantReceiptCommand Receipt(Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Receipt {suffix}", "Check", null, null,
            null, null, null, true, scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyPaymentsManage, $"receipt:{suffix}",
            $"tenant-receipt:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static ReverseTenantChargeCommand ReverseCharge(Scenario scenario, long entryId, string suffix) =>
        new(scenario.PortfolioId, scenario.AccountId, entryId, DateOnly.FromDateTime(DateTime.UtcNow),
            $"Correction {suffix}", null, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"charge-reversal:{suffix}",
            $"tenant-charge-reversal:{scenario.PortfolioId}:{scenario.AccountId}:{entryId}:{suffix}");

    private async Task<long> SeedLedgerEntryAsync(
        Scenario scenario,
        int accountId,
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction,
        string businessKey,
        decimal amount,
        DateOnly? dueOn = null)
    {
        await using var db = NewContext();
        var entry = new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = accountId,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
            DueOn = dueOn,
            PostedAtUtc = DateTime.UtcNow,
            Description = businessKey,
            BusinessKey = businessKey,
            CreatedByUserId = scenario.UserId,
        };
        db.TenantLedgerEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private CompanionFailureInterceptor Failures =>
        _services!.GetRequiredService<CompanionFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for tenant charge PostgreSQL tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:tenant-charge";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CompanionFailureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public bool FailAtomicAudit { get; set; }
        public IReadOnlyCollection<string> Commands => _commands.ToArray();

        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private void MaybeFail(string sql)
        {
            _commands.Enqueue(sql);
            if (FailAtomicAudit
                && sql.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected tenant-charge audit failure");
            }
        }
    }

    private sealed record Scenario(
        int PortfolioId,
        int PropertyId,
        int AccountId,
        int OtherAccountId,
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision);
}
