using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public sealed class HistoricalRentRecoveryPostgreSqlTests
{
    private const string ApiPassword = "historical-rent-api-role-test-password";
    private static readonly DateTime SeededAtUtc =
        new(2027, 01, 08, 14, 30, 00, DateTimeKind.Utc);
    private static readonly DateTime InterceptorNow =
        new(2099, 08, 20, 12, 00, 00, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;

    public HistoricalRentRecoveryPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Recovery_RepairsProratedJanuaryRent_Replays_AndRollsBackCompanionFailure()
    {
        var failure = new OutboxFailureInterceptor();
        await using var setup = await _fixture.CreateContextAsync([failure]);
        var scope = setup.Db.SeedAdministratorScope(
            1,
            nameof(Recovery_RepairsProratedJanuaryRent_Replays_AndRollsBackCompanionFailure));
        SeedFrozenBusinessDate(setup.Db);
        var graph = await SeedL035ProratedRentAsync(setup.Db, scope.UserId);
        await AssertRuntimeTenantMoneyGrantMigrationAppliedAsync(setup.Db, "rentalcommand_api");
        await setup.Db.Database.ExecuteSqlRawAsync(
            $"ALTER ROLE rentalcommand_api PASSWORD '{ApiPassword}';");
        var apiConnectionString = ApiConnectionString(setup.ConnectionString);
        var requestScope = new RequestGucConnectionInterceptor(scope);
        await using var services = Services(apiConnectionString, failure, requestScope);
        await using var atomicScope = services.CreateAsyncScope();
        var db = atomicScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = atomicScope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        Task<AtomicCommandOutcome<RecoverHistoricalRentChargeResult>> Execute(
            RecoverHistoricalRentChargeCommand value)
        {
            var handler = new RecoverHistoricalRentChargeRule(db);
            return writes.ExecuteAsync(
                value.DeliveryIdempotencyKey,
                TenantMoneyWriteSupport.Write(
                    value, handler.ExecuteAsync, handler.AuthorizeAsync));
        }

        var conflict = Command(scope, graph, "conflict") with { ExpectedExistingChargeAmount = 1_377.41m };
        await FluentActions.Invoking(() => Execute(conflict))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*prorated charge*");
        await AssertOriginalStateAsync(setup.Db, graph);

        var rollback = Command(scope, graph, "rollback");
        failure.FailNextOutboxInsert = true;
        await FluentActions.Invoking(() => Execute(rollback))
            .Should().ThrowAsync<Exception>();
        await AssertOriginalStateAsync(setup.Db, graph);
        (await setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "tenant-money", rollback.DeliveryIdempotencyKey))).Should().Be(0);
        (await setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == Identity(rollback).CommandType
            && row.CommandIdempotencyKey == Identity(rollback).IdempotencyKey)).Should().Be(0);
        (await setup.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == Identity(rollback).CommandType
            && row.IdempotencyKey == Identity(rollback).IdempotencyKey)).Should().Be(0);

        var command = Command(scope, graph, "success");
        var first = await Execute(command);
        var replay = await Execute(command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Applied.Should().BeTrue();
        first.Value.TenantAccountId.Should().Be(graph.TenantAccountId);
        first.Value.LeaseAgreementId.Should().Be(graph.LeaseAgreementId);
        first.Value.ReversedRentChargeEntryId.Should().Be(graph.ProratedRentEntryId);
        first.Value.ReceiptEntryId.Should().Be(graph.ReceiptEntryId);
        first.Value.RentTrackingStartOn.Should().Be(new DateOnly(2027, 01, 01));
        first.Value.ReversedRentAmount.Should().Be(1_377.42m);
        first.Value.ReplacementRentAmount.Should().Be(1_525m);
        first.Value.ReallocatedAmount.Should().Be(1_525m);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        setup.Db.ChangeTracker.Clear();
        var account = await setup.Db.TenantAccounts
            .AsNoTracking()
            .SingleAsync(row => row.Id == graph.TenantAccountId);
        account.RentTrackingStartOn.Should().Be(new DateOnly(2027, 01, 01));

        var entries = await setup.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(row => row.TenantAccountId == graph.TenantAccountId)
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Id,
                row.EntryType,
                row.Direction,
                row.Amount,
                row.EffectiveOn,
                row.DueOn,
                row.ReversesEntryId,
                row.LeaseAgreementId,
                row.BusinessKey,
            })
            .ToListAsync();
        entries.Should().HaveCount(4);
        entries.Should().Contain(row =>
            row.Id == first.Value.ReversalEntryId
            && row.EntryType == TenantLedgerEntryType.Reversal
            && row.Direction == TenantLedgerDirection.Credit
            && row.Amount == 1_377.42m
            && row.ReversesEntryId == graph.ProratedRentEntryId);
        entries.Should().Contain(row =>
            row.Id == first.Value.ReplacementRentChargeEntryId
            && row.EntryType == TenantLedgerEntryType.RentCharge
            && row.Direction == TenantLedgerDirection.Debit
            && row.Amount == 1_525m
            && row.EffectiveOn == new DateOnly(2027, 01, 01)
            && row.DueOn == new DateOnly(2027, 01, 01)
            && row.LeaseAgreementId == graph.LeaseAgreementId
            && row.BusinessKey == "historical-rent:FIN-L035-JAN-2027:replacement");

        var allocations = await setup.Db.TenantLedgerAllocations
            .AsNoTracking()
            .Where(row => row.TenantAccountId == graph.TenantAccountId)
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Id,
                row.DebitEntryId,
                row.CreditEntryId,
                row.Amount,
                row.EffectiveOn,
                row.ReversesAllocationId,
                row.BusinessKey,
            })
            .ToListAsync();
        allocations.Should().HaveCount(3);
        allocations.Should().Contain(row =>
            row.Id == first.Value.ReversedAllocationId
            && row.Amount == -1_377.42m
            && row.ReversesAllocationId == graph.AllocationId
            && row.EffectiveOn == new DateOnly(2027, 01, 01));
        allocations.Should().Contain(row =>
            row.Id == first.Value.ReplacementAllocationId
            && row.DebitEntryId == first.Value.ReplacementRentChargeEntryId
            && row.CreditEntryId == graph.ReceiptEntryId
            && row.Amount == 1_525m);

        var balance = await setup.Db.TenantChargeBalanceProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == 1
                && row.TenantAccountId == graph.TenantAccountId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Sum(row => row.OriginalAmount),
                Open = group.Sum(row => row.OpenAmount),
            })
            .SingleAsync();
        balance.Total.Should().Be(2_902.42m);
        balance.Open.Should().Be(0m);

        SetFrozenBusinessDate(setup.Db, new DateTime(2026, 12, 31, 12, 00, 00, DateTimeKind.Utc));
        var beforeRecoveryBoundary = await setup.Db.TenantAccountBalanceProjections
            .AsNoTracking()
            .SingleAsync(row => row.TenantAccountId == graph.TenantAccountId);
        beforeRecoveryBoundary.BusinessDate.Should().Be(new DateOnly(2026, 12, 31));
        beforeRecoveryBoundary.UnappliedCredit.Should().Be(0m);

        SetFrozenBusinessDate(setup.Db, new DateTime(2027, 01, 01, 12, 00, 00, DateTimeKind.Utc));
        var onRecoveryBoundary = await setup.Db.TenantAccountBalanceProjections
            .AsNoTracking()
            .SingleAsync(row => row.TenantAccountId == graph.TenantAccountId);
        onRecoveryBoundary.BusinessDate.Should().Be(new DateOnly(2027, 01, 01));
        onRecoveryBoundary.UnappliedCredit.Should().Be(1_377.42m);

        (await setup.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == Identity(command).CommandType
            && row.IdempotencyKey == Identity(command).IdempotencyKey)).Should().Be(1);
        (await setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == Identity(command).CommandType
            && row.CommandIdempotencyKey == Identity(command).IdempotencyKey
            && row.EntityType == nameof(TenantAccount)
            && row.EntityId == graph.TenantAccountId)).Should().Be(1);
        (await setup.Db.AtomicAuditLogs
            .Where(row => row.CommandType == Identity(command).CommandType
                && row.CommandIdempotencyKey == Identity(command).IdempotencyKey)
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync()).Should().OnlyContain(timestamp => timestamp == SeededAtUtc);
        (await setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "tenant-money",
            command.DeliveryIdempotencyKey))).Should().Be(1);
        var outboxPayload = await setup.Db.OutboxMessages
            .AsNoTracking()
            .Where(row => row.IdempotencyKey == OutboxIdempotency.Create(
                "tenant-money", command.DeliveryIdempotencyKey))
            .Select(row => row.Payload)
            .SingleAsync();
        using var outboxDocument = JsonDocument.Parse(outboxPayload);
        outboxDocument.RootElement.GetProperty("operation").GetString()
            .Should().Be("historical-rent-recovery");
        var outboxData = outboxDocument.RootElement.GetProperty("data");
        outboxData.GetProperty("ReplacementRentChargeEntryId").GetInt64()
            .Should().Be(first.Value.ReplacementRentChargeEntryId);
        outboxData.GetProperty("ReceiptEntryId").GetInt64()
            .Should().Be(graph.ReceiptEntryId);
    }

    [Fact]
    public async Task RefundedAllocationRecovery_AppendsExactCompensation_Replays_AndRollsBackCompanions()
    {
        var failure = new OutboxFailureInterceptor();
        await using var setup = await _fixture.CreateContextAsync([failure]);
        var scope = setup.Db.SeedAdministratorScope(
            1,
            nameof(RefundedAllocationRecovery_AppendsExactCompensation_Replays_AndRollsBackCompanions));
        SeedFrozenBusinessDate(setup.Db);
        var graph = await SeedL035ProratedRentAsync(
            setup.Db,
            scope.UserId,
            fullyRefundReceipt: true);
        await AssertRuntimeTenantMoneyGrantMigrationAppliedAsync(setup.Db, "rentalcommand_api");
        await setup.Db.Database.ExecuteSqlRawAsync(
            $"ALTER ROLE rentalcommand_api PASSWORD '{ApiPassword}';");
        var requestScope = new RequestGucConnectionInterceptor(scope);
        await using var services = Services(
            ApiConnectionString(setup.ConnectionString),
            failure,
            requestScope);
        await using var atomicScope = services.CreateAsyncScope();
        var db = atomicScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = atomicScope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        Task<AtomicCommandOutcome<RecoverRefundedTenantAllocationResult>> Execute(
            RecoverRefundedTenantAllocationCommand value)
        {
            var handler = new RecoverRefundedTenantAllocationRule(db);
            return writes.ExecuteAsync(
                value.DeliveryIdempotencyKey,
                TenantMoneyWriteSupport.Write(
                    value, handler.ExecuteAsync, handler.AuthorizeAsync));
        }

        var mismatch = RefundedAllocationCommand(scope, graph, "mismatch") with
        {
            ExpectedRefundAmount = 1_524m,
        };
        await FluentActions
            .Invoking(() => Execute(mismatch))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no longer matches*");

        var rollback = RefundedAllocationCommand(scope, graph, "rollback");
        failure.FailNextOutboxInsert = true;
        await FluentActions
            .Invoking(() => Execute(rollback))
            .Should().ThrowAsync<Exception>();
        setup.Db.ChangeTracker.Clear();
        (await setup.Db.TenantLedgerAllocations.CountAsync(row =>
            row.ReversesAllocationId == graph.AllocationId)).Should().Be(0);

        var command = RefundedAllocationCommand(scope, graph, "success");
        var first = await Execute(command);
        var replay = await Execute(command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Applied.Should().BeTrue();
        first.Value.ReversedAllocationId.Should().Be(graph.AllocationId);
        first.Value.DebitEntryId.Should().Be(graph.ProratedRentEntryId);
        first.Value.CreditEntryId.Should().Be(graph.ReceiptEntryId);
        first.Value.RefundPaymentAttemptId.Should().Be(graph.RefundPaymentAttemptId);
        first.Value.ReversedAmount.Should().Be(1_377.42m);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        setup.Db.ChangeTracker.Clear();
        var compensation = await setup.Db.TenantLedgerAllocations
            .AsNoTracking()
            .SingleAsync(row => row.ReversesAllocationId == graph.AllocationId);
        compensation.Id.Should().Be(first.Value.ReversalAllocationId);
        compensation.Amount.Should().Be(-1_377.42m);
        compensation.DebitEntryId.Should().Be(graph.ProratedRentEntryId);
        compensation.CreditEntryId.Should().Be(graph.ReceiptEntryId);
        compensation.EffectiveOn.Should().Be(new DateOnly(2027, 01, 06));
        compensation.BusinessKey.Should().Be(
            $"refunded-allocation-recovery:{scope.PortfolioId}:{graph.TenantAccountId}:{graph.AllocationId}");

        SetFrozenBusinessDate(setup.Db, new DateTime(2027, 01, 05, 12, 00, 00, DateTimeKind.Utc));
        var beforeRefundBoundary = await setup.Db.TenantAccountBalanceProjections
            .AsNoTracking()
            .SingleAsync(row => row.TenantAccountId == graph.TenantAccountId);
        beforeRefundBoundary.BusinessDate.Should().Be(new DateOnly(2027, 01, 05));
        beforeRefundBoundary.UnappliedCredit.Should().Be(147.58m);

        SetFrozenBusinessDate(setup.Db, new DateTime(2027, 01, 06, 12, 00, 00, DateTimeKind.Utc));
        var onRefundBoundary = await setup.Db.TenantAccountBalanceProjections
            .AsNoTracking()
            .SingleAsync(row => row.TenantAccountId == graph.TenantAccountId);
        onRefundBoundary.BusinessDate.Should().Be(new DateOnly(2027, 01, 06));
        onRefundBoundary.UnappliedCredit.Should().Be(0m);
        (await setup.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == RefundedAllocationIdentity(command).CommandType
            && row.IdempotencyKey == RefundedAllocationIdentity(command).IdempotencyKey))
            .Should().Be(1);
        (await setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == RefundedAllocationIdentity(command).CommandType
            && row.CommandIdempotencyKey == RefundedAllocationIdentity(command).IdempotencyKey))
            .Should().Be(1);
        (await setup.Db.AtomicAuditLogs
            .Where(row => row.CommandType == RefundedAllocationIdentity(command).CommandType
                && row.CommandIdempotencyKey == RefundedAllocationIdentity(command).IdempotencyKey)
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync()).Should().OnlyContain(timestamp => timestamp == SeededAtUtc);
        (await setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "tenant-money",
                command.DeliveryIdempotencyKey))).Should().Be(1);
    }

    private static ServiceProvider Services(
        string connectionString,
        OutboxFailureInterceptor failure,
        RequestGucConnectionInterceptor requestScope)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(InterceptorNow));
        services.AddSingleton(failure);
        services.AddSingleton(requestScope);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .AddInterceptors(
                    provider.GetRequiredService<RequestGucConnectionInterceptor>(),
                    provider.GetRequiredService<OutboxFailureInterceptor>())
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static string ApiConnectionString(string ownerConnectionString) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = "rentalcommand_api",
            Password = ApiPassword,
            Pooling = false,
        }.ConnectionString;

    private static AtomicCommandIdentity Identity(RecoverHistoricalRentChargeCommand command) =>
        new("historical-rent-charge.recover", command.DeliveryIdempotencyKey);

    private static AtomicCommandIdentity RefundedAllocationIdentity(
        RecoverRefundedTenantAllocationCommand command) =>
        new("refunded-tenant-allocation.recover", command.DeliveryIdempotencyKey);

    private static async Task AssertRuntimeTenantMoneyGrantMigrationAppliedAsync(
        RentalCommandDbContext db,
        string role)
    {
        var applied = await db.Database.SqlQuery<int>($"""
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260728235000_GrantRuntimeTenantMoneyDml')
              AND EXISTS (
                SELECT 1
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260728235500_GrantRuntimeTenantMoneyRowLockUpdates')
              AND has_table_privilege({role}, '"TenantAccounts"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'INSERT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'UPDATE')
              AND has_table_privilege({role}, '"TenantLedgerAllocations"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerAllocations"', 'INSERT')
              AND has_table_privilege({role}, '"TenantLedgerAllocations"', 'UPDATE')
              AND has_table_privilege({role}, '"TenantAccounts"', 'UPDATE')
              AND has_sequence_privilege({role}, '"TenantLedgerEntries_Id_seq"', 'USAGE')
              AND has_sequence_privilege({role}, '"TenantLedgerAllocations_Id_seq"', 'USAGE')
            THEN 1 ELSE 0 END AS "Value"
            """).SingleAsync();

        applied.Should().Be(1);
    }

    private static RecoverHistoricalRentChargeCommand Command(
        WorkspaceReadScope scope,
        L035Graph graph,
        string key) =>
        new(
            scope.PortfolioId,
            graph.TenantAccountId,
            graph.LeaseAgreementId,
            graph.ProratedRentEntryId,
            graph.ReceiptEntryId,
            graph.AllocationId,
            new DateOnly(2027, 01, 04),
            new DateOnly(2027, 01, 01),
            new DateOnly(2027, 01, 01),
            new DateOnly(2027, 01, 04),
            1_377.42m,
            1_525m,
            1_525m,
            "FIN-L035-JAN-2027",
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            $"historical-rent:{scope.PortfolioId}:{graph.TenantAccountId}:{key}");

    private static RecoverRefundedTenantAllocationCommand RefundedAllocationCommand(
        WorkspaceReadScope scope,
        L035Graph graph,
        string key) =>
        new(
            scope.PortfolioId,
            graph.TenantAccountId,
            graph.AllocationId,
            graph.ProratedRentEntryId,
            graph.ReceiptEntryId,
            graph.RefundPaymentAttemptId
                ?? throw new InvalidOperationException("Refunded recovery graph is incomplete."),
            1_377.42m,
            1_525m,
            "FIN-REFUNDED-ALLOCATION-YS221",
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            $"refunded-allocation-recovery:{scope.PortfolioId}:{graph.TenantAccountId}:{graph.AllocationId}",
            $"refunded-allocation-recovery:{scope.PortfolioId}:{graph.TenantAccountId}:{key}");

    private static async Task AssertOriginalStateAsync(RentalCommandDbContext db, L035Graph graph)
    {
        db.ChangeTracker.Clear();
        var accountStart = await db.TenantAccounts
            .AsNoTracking()
            .Where(row => row.Id == graph.TenantAccountId)
            .Select(row => row.RentTrackingStartOn)
            .SingleAsync();
        accountStart.Should().Be(new DateOnly(2027, 01, 04));
        (await db.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(2);
        (await db.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(1);
    }

    private static async Task<L035Graph> SeedL035ProratedRentAsync(
        RentalCommandDbContext db,
        int actorUserId,
        bool fullyRefundReceipt = false)
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = "L035 Recovery Property",
            AddressLine1 = "35 Ledger Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = "L035",
            MarketRent = 1_525m,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Lease",
            LastName = "Thirtyfive",
            Email = $"l035-{Guid.NewGuid():N}@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-L035-{Guid.NewGuid():N}"[..18],
            PossessionGivenAtUtc = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = SeededAtUtc.AddDays(-30),
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = actorUserId,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = $"TA-L035-{Guid.NewGuid():N}"[..18],
            Currency = "USD",
            RentTrackingStartOn = new DateOnly(2027, 01, 04),
            OpenedAtUtc = SeededAtUtc.AddDays(-30),
            CreatedAtUtc = SeededAtUtc.AddDays(-30),
            CreatedByUserId = actorUserId,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 06, 01),
            ChangeReason = "Historical rent recovery setup",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = actorUserId,
        };
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"l035-source:{Guid.NewGuid():N}",
            RendererKey = "l035-lease",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = actorUserId,
        };
        db.AddRange(property, unit, tenant, relationship, account, party, source);
        await db.SaveChangesAsync();

        var issuedFile = StoredFile("l035-issued.pdf");
        var executedFile = StoredFile("l035-executed.pdf");
        db.StoredFiles.AddRange(issuedFile, executedFile);
        await db.SaveChangesAsync();
        var issuedArtifact = AgreementArtifact(
            issuedFile,
            LegalDocumentArtifactKind.IssuedAgreement,
            new string('a', 64),
            new string('b', 64),
            actorUserId);
        var executedArtifact = AgreementArtifact(
            executedFile,
            LegalDocumentArtifactKind.ExecutedAgreement,
            new string('c', 64),
            null,
            actorUserId);
        db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"AGR-L035-{Guid.NewGuid():N}"[..18],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 06, 01),
            TermEndOn = new DateOnly(2027, 05, 31),
            GoverningFromOn = new DateOnly(2026, 06, 01),
            BaseRentAmount = 1_525m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_525m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = SeededAtUtc.AddDays(-20),
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = actorUserId,
        };
        var signer = new LeaseAgreementSigner
        {
            PortfolioId = 1,
            LeaseAgreement = agreement,
            LeaseManagementParty = party,
            Tenant = tenant,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        };
        db.AddRange(agreement, signer);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = SeededAtUtc.AddDays(-21);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = SeededAtUtc.AddDays(-20);
        agreement.UpdatedAtUtc = SeededAtUtc;
        await db.SaveChangesAsync();

        var paymentTransaction = fullyRefundReceipt
            ? await db.Database.BeginTransactionAsync()
            : null;
        TenantPaymentAttempt? paymentAttempt = null;
        if (fullyRefundReceipt)
        {
            paymentAttempt = new TenantPaymentAttempt
            {
                PortfolioId = 1,
                TenantAccount = account,
                Provider = "manual",
                ProviderObjectId = $"l035-payment:{Guid.NewGuid():N}",
                IdempotencyKey = $"l035-payment:{Guid.NewGuid():N}",
                AttemptType = TenantPaymentAttemptType.UnappliedReceipt,
                State = TenantPaymentAttemptState.Succeeded,
                Amount = 1_525m,
                Currency = "USD",
                PaymentMethodSummary = "Manual payment",
                PreparedAtUtc = SeededAtUtc.AddDays(-3),
                SubmittedAtUtc = SeededAtUtc.AddDays(-3),
                SettledAtUtc = SeededAtUtc.AddDays(-3),
                UpdatedAtUtc = SeededAtUtc.AddDays(-3),
                AttemptCount = 1,
                CreatedByUserId = actorUserId,
            };
            db.TenantPaymentAttempts.Add(paymentAttempt);
            await db.SaveChangesAsync();
        }

        var rent = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_377.42m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 04),
            DueOn = new DateOnly(2027, 01, 04),
            PostedAtUtc = SeededAtUtc,
            Description = "Rent due Jan 4, 2027",
            BusinessKey = $"rent:{agreement.PublicId}:2027-01",
            LeaseAgreement = agreement,
            CreatedByUserId = actorUserId,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1_525m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 04),
            PostedAtUtc = SeededAtUtc,
            Description = "January rent receipt",
            BusinessKey = $"receipt:l035:{Guid.NewGuid():N}",
            ProviderPaymentAttemptId = paymentAttempt?.Id,
            CreatedByUserId = actorUserId,
        };
        db.TenantLedgerEntries.AddRange(rent, receipt);
        await db.SaveChangesAsync();
        if (paymentTransaction is not null)
        {
            await paymentTransaction.CommitAsync();
            await paymentTransaction.DisposeAsync();
        }

        var allocation = new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccount = account,
            DebitEntryId = rent.Id,
            CreditEntryId = receipt.Id,
            Amount = 1_377.42m,
            AllocatedAtUtc = SeededAtUtc,
            BusinessKey = $"allocation:l035:{Guid.NewGuid():N}",
            CreatedByUserId = actorUserId,
        };
        db.TenantLedgerAllocations.Add(allocation);
        await db.SaveChangesAsync();

        long? refundPaymentAttemptId = null;
        if (paymentAttempt is not null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var refundAttempt = new TenantPaymentAttempt
            {
                PortfolioId = 1,
                TenantAccount = account,
                Provider = "manual",
                ProviderObjectId = $"l035-refund:{Guid.NewGuid():N}",
                RefundsPaymentAttemptId = paymentAttempt.Id,
                IdempotencyKey = $"l035-refund:{Guid.NewGuid():N}",
                AttemptType = TenantPaymentAttemptType.Refund,
                State = TenantPaymentAttemptState.Succeeded,
                Amount = 1_525m,
                Currency = "USD",
                PaymentMethodSummary = "Manual refund",
                PreparedAtUtc = SeededAtUtc.AddDays(-2),
                SubmittedAtUtc = SeededAtUtc.AddDays(-2),
                SettledAtUtc = SeededAtUtc.AddDays(-2),
                UpdatedAtUtc = SeededAtUtc.AddDays(-2),
                AttemptCount = 1,
                CreatedByUserId = actorUserId,
            };
            db.TenantPaymentAttempts.Add(refundAttempt);
            await db.SaveChangesAsync();
            db.TenantLedgerEntries.Add(new TenantLedgerEntry
            {
                PortfolioId = 1,
                TenantAccount = account,
                EntryType = TenantLedgerEntryType.Refund,
                Direction = TenantLedgerDirection.Debit,
                Amount = 1_525m,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 01, 06),
                PostedAtUtc = SeededAtUtc.AddDays(-2),
                Description = "Fully refunded January receipt",
                BusinessKey = $"refund:l035:{Guid.NewGuid():N}",
                ProviderPaymentAttemptId = refundAttempt.Id,
                CreatedByUserId = actorUserId,
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            refundPaymentAttemptId = refundAttempt.Id;
        }
        db.ChangeTracker.Clear();

        return new L035Graph(
            account.Id,
            agreement.Id,
            rent.Id,
            receipt.Id,
            allocation.Id,
            refundPaymentAttemptId);
    }

    private static void SeedFrozenBusinessDate(RentalCommandDbContext db) =>
        SetFrozenBusinessDate(db, SeededAtUtc);

    private static void SetFrozenBusinessDate(RentalCommandDbContext db, DateTime frozenAtUtc)
    {
        var clock = db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = db.SimulationClocks.Local.Single(clock => clock.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = frozenAtUtc;
        clock.RealAnchorUtc = frozenAtUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = frozenAtUtc;
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static StoredFile StoredFile(string fileName) => new()
    {
        PortfolioId = 1,
        FileName = fileName,
        FilePath = $"test/{Guid.NewGuid():N}/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        ContentSha256 = new string(fileName.Contains("issued", StringComparison.Ordinal) ? 'a' : 'c', 64),
        UploadedAt = SeededAtUtc,
    };

    private static LegalDocumentArtifact AgreementArtifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        string contentSha,
        string? issuanceFingerprint,
        int actorUserId) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = contentSha,
        LegalIssuanceFingerprint = issuanceFingerprint,
        CreatedAtUtc = SeededAtUtc,
        CreatedByUserId = actorUserId,
    };

    private sealed record L035Graph(
        int TenantAccountId,
        int LeaseAgreementId,
        long ProratedRentEntryId,
        long ReceiptEntryId,
        long AllocationId,
        long? RefundPaymentAttemptId);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
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
                && command.CommandText.Contains(
                    "INSERT INTO \"OutboxMessages\"",
                    StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InvalidOperationException("Injected outbox failure.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RequestGucConnectionInterceptor(WorkspaceReadScope scope) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT set_config('app.current_portfolio_id', @portfolio_id, false),
                       set_config('app.auth_session_id', @auth_session_id, false),
                       set_config('app.current_user_id', @user_id, false),
                       set_config('app.current_access_context_id', @access_context_id, false),
                       set_config('app.access_revision', @access_revision, false);
                """;
            AddParameter(command, "portfolio_id", scope.PortfolioId.ToString());
            AddParameter(command, "auth_session_id", scope.SessionId.ToString());
            AddParameter(command, "user_id", scope.UserId.ToString());
            AddParameter(command, "access_context_id", scope.AccessContextId.ToString());
            AddParameter(command, "access_revision", scope.AccessRevision.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddParameter(DbCommand command, string name, string value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:historical-rent-recovery";
        public string? IpAddress => "127.0.0.1";
    }
}
