using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auth;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class RecurringTenantChargeAtomicPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _setup = null!;
    private ServiceProvider _services = null!;

    public RecurringTenantChargeAtomicPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(_setup.Db).SeedAsync(PortfolioId);
        await _setup.Db.SaveChangesAsync();
        _services = CreateServices(_setup.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _setup.DisposeAsync();
    }

    [Fact]
    public async Task Create_DerivesTenantDimensions_AndWritesAuditAndOutbox()
    {
        var graph = await SeedGraphAsync("create-derived");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "create-derived-manager");
        var command = CreateCommand(graph, scope, "create-derived-key") with
        {
            BusinessNowUtc = DateTime.UtcNow.AddHours(1),
        };

        var outcome = await Execute(
            "tenant-account.recurring-charge.create.v1", command.DeliveryIdempotencyKey, command);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        outcome.Value.Outcome.Should().Be(RecurringTenantChargeMutationOutcome.Created);
        outcome.Value.Snapshot.Should().NotBeNull();
        outcome.Value.Snapshot!.PropertyId.Should().Be(graph.PropertyId);
        outcome.Value.Snapshot.UnitId.Should().Be(graph.UnitId);
        outcome.Value.Snapshot.Currency.Should().Be("USD");

        _setup.Db.ChangeTracker.Clear();
        var schedule = await _setup.Db.RecurringTenantCharges.AsNoTracking()
            .SingleAsync(row => row.Id == outcome.Value.EntityId);
        schedule.PropertyId.Should().Be(graph.PropertyId);
        schedule.UnitId.Should().Be(graph.UnitId);
        schedule.Currency.Should().Be("USD");

        var audit = await _setup.Db.AtomicAuditLogs.AsNoTracking()
            .SingleAsync(row => row.EntityType == nameof(RecurringTenantCharge)
                && row.EntityId == schedule.Id);
        audit.Operation.Should().Be(AuditLogOperation.Created);
        audit.UserId.Should().Be(scope.UserId);
        audit.Timestamp.Should().Be(command.BusinessNowUtc)
            .And.NotBeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        audit.NewValues.Should().Contain($"\"PropertyId\": {graph.PropertyId}");
        audit.NewValues.Should().Contain($"\"UnitId\": {graph.UnitId}");

        var outbox = await _setup.Db.OutboxMessages.AsNoTracking()
            .SingleAsync(row => row.IdempotencyKey.StartsWith("recurring-tenant-charge-config:"));
        outbox.MessageType.Should().Be("data-update");
        outbox.Payload.Should().Contain(nameof(RecurringTenantCharge));
    }

    [Fact]
    public async Task Create_ExactReplay_ReturnsRecordedResult_AndDoesNotDuplicateEvidence()
    {
        var graph = await SeedGraphAsync("create-replay");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "create-replay-manager");
        var command = CreateCommand(graph, scope, "create-replay-key");

        var first = await Execute(
            "tenant-account.recurring-charge.create.v1", command.DeliveryIdempotencyKey, command);
        var replay = await Execute(
            "tenant-account.recurring-charge.create.v1", command.DeliveryIdempotencyKey, command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _setup.Db.ChangeTracker.Clear();
        (await _setup.Db.RecurringTenantCharges.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(1);
        (await _setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(RecurringTenantCharge))).Should().Be(1);
        (await _setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith("recurring-tenant-charge-config:"))).Should().Be(1);
    }

    [Fact]
    public async Task Create_RequiresActiveIncomeAccount_AndLeaseOnTenantRelationship()
    {
        var graph = await SeedGraphAsync("create-rules");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "create-rules-manager");
        var inactiveIncome = new LedgerAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            Code = $"49{Guid.NewGuid():N}"[..10],
            Name = "Inactive recurring income",
            AccountType = AccountType.Income,
            NormalBalance = NormalBalance.Credit,
            IsActive = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        _setup.Db.LedgerAccounts.Add(inactiveIncome);
        await _setup.Db.SaveChangesAsync();

        var inactiveCommand = CreateCommand(
            graph, scope, "create-inactive-income", ledgerAccountId: inactiveIncome.Id);
        await FluentActions.Invoking(() => Execute(
                "tenant-account.recurring-charge.create.v1",
                inactiveCommand.DeliveryIdempotencyKey, inactiveCommand))
            .Should().ThrowAsync<ArgumentException>();

        var wrongTypeCommand = CreateCommand(
            graph, scope, "create-expense-account", ledgerAccountId: graph.ExpenseAccountId);
        await FluentActions.Invoking(() => Execute(
                "tenant-account.recurring-charge.create.v1",
                wrongTypeCommand.DeliveryIdempotencyKey, wrongTypeCommand))
            .Should().ThrowAsync<ArgumentException>();

        var wrongLeaseCommand = CreateCommand(
            graph, scope, "create-wrong-lease", leaseAgreementId: graph.OtherLeaseAgreementId);
        await FluentActions.Invoking(() => Execute(
                "tenant-account.recurring-charge.create.v1",
                wrongLeaseCommand.DeliveryIdempotencyKey, wrongLeaseCommand))
            .Should().ThrowAsync<ArgumentException>();

        _setup.Db.ChangeTracker.Clear();
        (await _setup.Db.RecurringTenantCharges.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(0);
        (await _setup.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == "tenant-account.recurring-charge.create.v1"
            && row.IdempotencyKey.StartsWith("create-"))).Should().Be(0);
    }

    [Fact]
    public async Task Create_RequiresMoneyChargesManageOnTenantAccountProperty()
    {
        var graph = await SeedGraphAsync("create-auth");
        var wrongPropertyScope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.DecoyPropertyId, "create-auth-wrong-property");
        var wrongPropertyCommand = CreateCommand(
            graph, wrongPropertyScope, "create-wrong-property");

        await FluentActions.Invoking(() => Execute(
                "tenant-account.recurring-charge.create.v1",
                wrongPropertyCommand.DeliveryIdempotencyKey, wrongPropertyCommand))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        var leasingScope = _setup.Db.SeedLeasingAgentScope(
            PortfolioId, graph.PropertyId, "create-auth-leasing-agent");
        var missingCapabilityCommand = CreateCommand(
            graph, leasingScope, "create-missing-capability");

        await FluentActions.Invoking(() => Execute(
                "tenant-account.recurring-charge.create.v1",
                missingCapabilityCommand.DeliveryIdempotencyKey, missingCapabilityCommand))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        _setup.Db.ChangeTracker.Clear();
        (await _setup.Db.RecurringTenantCharges.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(0);
    }

    [Fact]
    public async Task Update_ChangesConfigurationForFutureOccurrencesOnly_AndReplaysExactly()
    {
        var graph = await SeedGraphAsync("update-future");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "update-future-manager");
        var create = CreateCommand(graph, scope, "update-future-create");
        var created = await Execute(
            "tenant-account.recurring-charge.create.v1", create.DeliveryIdempotencyKey, create);

        var priorOccurrence = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 275m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 1, 31),
            DueOn = new DateOnly(2027, 1, 31),
            PostedAtUtc = DateTime.UtcNow,
            Description = "Already posted recurring charge",
            BusinessKey = $"recurring-tenant-charge:{created.Value.EntityId}:2027-01",
            LeaseAgreementId = graph.LeaseAgreementId,
            CreatedByUserId = 1,
        };
        _setup.Db.TenantLedgerEntries.Add(priorOccurrence);
        await _setup.Db.SaveChangesAsync();

        var update = new UpdateRecurringTenantChargeCommand(
            PortfolioId,
            Actor(scope),
            graph.TenantAccountId,
            created.Value.EntityId,
            "Updated pet rent",
            325m,
            graph.IncomeAccountId,
            new DateOnly(2027, 2, 1),
            null,
            15,
            new DateOnly(2027, 2, 15),
            DateTime.UtcNow,
            "update-future-key");

        var first = await Execute(
            "tenant-account.recurring-charge.update.v1", update.DeliveryIdempotencyKey, update);
        var replay = await Execute(
            "tenant-account.recurring-charge.update.v1", update.DeliveryIdempotencyKey, update);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Outcome.Should().Be(RecurringTenantChargeMutationOutcome.Updated);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _setup.Db.ChangeTracker.Clear();
        var schedule = await _setup.Db.RecurringTenantCharges.AsNoTracking()
            .SingleAsync(row => row.Id == created.Value.EntityId);
        schedule.DisplayName.Should().Be("Updated pet rent");
        schedule.Amount.Should().Be(325m);
        schedule.EffectiveStartOn.Should().Be(new DateOnly(2027, 2, 1));
        schedule.MonthlyDueDay.Should().Be(15);
        schedule.NextRunDate.Should().Be(new DateOnly(2027, 2, 15));
        schedule.Currency.Should().Be("USD");
        schedule.PropertyId.Should().Be(graph.PropertyId);
        schedule.UnitId.Should().Be(graph.UnitId);

        var storedOccurrence = await _setup.Db.TenantLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.Id == priorOccurrence.Id);
        storedOccurrence.Amount.Should().Be(275m);
        storedOccurrence.EffectiveOn.Should().Be(new DateOnly(2027, 1, 31));
        storedOccurrence.BusinessKey.Should().Be(
            $"recurring-tenant-charge:{created.Value.EntityId}:2027-01");

        (await _setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(RecurringTenantCharge)
            && row.EntityId == created.Value.EntityId)).Should().Be(2);
        (await _setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith("recurring-tenant-charge-config:"))).Should().Be(2);
    }

    [Fact]
    public async Task Deactivate_AuditsAndReplaysWithoutChangingDerivedDimensions()
    {
        var graph = await SeedGraphAsync("deactivate");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "deactivate-manager");
        var create = CreateCommand(graph, scope, "deactivate-create");
        var created = await Execute(
            "tenant-account.recurring-charge.create.v1", create.DeliveryIdempotencyKey, create);
        var deactivate = new DeactivateRecurringTenantChargeCommand(
            PortfolioId,
            Actor(scope),
            graph.TenantAccountId,
            created.Value.EntityId,
            DateTime.UtcNow,
            "deactivate-key");

        var first = await Execute(
            "tenant-account.recurring-charge.deactivate.v1",
            deactivate.DeliveryIdempotencyKey,
            deactivate);
        var replay = await Execute(
            "tenant-account.recurring-charge.deactivate.v1",
            deactivate.DeliveryIdempotencyKey,
            deactivate);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Outcome.Should().Be(RecurringTenantChargeMutationOutcome.Deactivated);
        first.Value.Snapshot!.IsActive.Should().BeFalse();
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _setup.Db.ChangeTracker.Clear();
        var schedule = await _setup.Db.RecurringTenantCharges.AsNoTracking()
            .SingleAsync(row => row.Id == created.Value.EntityId);
        schedule.IsActive.Should().BeFalse();
        schedule.Currency.Should().Be("USD");
        schedule.PropertyId.Should().Be(graph.PropertyId);
        schedule.UnitId.Should().Be(graph.UnitId);
        (await _setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(RecurringTenantCharge)
            && row.EntityId == created.Value.EntityId)).Should().Be(2);
        (await _setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(RecurringTenantCharge)
            && row.EntityId == created.Value.EntityId
            && row.Operation == AuditLogOperation.Updated)).Should().Be(1);
        (await _setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith("recurring-tenant-charge-config:"))).Should().Be(2);
    }

    [Fact]
    public async Task Create_LateAuditFailureRollsBackScheduleReceiptAuditAndOutbox()
    {
        var graph = await SeedGraphAsync("create-rollback");
        var scope = _setup.Db.SeedPropertyManagerScope(
            PortfolioId, graph.PropertyId, "create-rollback-manager");
        var command = CreateCommand(graph, scope, "create-rollback-key");
        var failure = new AuditInsertFailureInterceptor { FailAtomicAudit = true };
        await using var services = CreateServices(_setup.ConnectionString, failure);

        await FluentActions.Invoking(() => ExecuteCreateAsync(services, command))
            .Should().ThrowAsync<DbUpdateException>();

        _setup.Db.ChangeTracker.Clear();
        (await _setup.Db.RecurringTenantCharges.CountAsync(row =>
            row.TenantAccountId == graph.TenantAccountId)).Should().Be(0);
        (await _setup.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == "tenant-account.recurring-charge.create.v1"
            && row.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(0);
        (await _setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(RecurringTenantCharge))).Should().Be(0);
        (await _setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith("recurring-tenant-charge-config:"))).Should().Be(0);

        failure.FailAtomicAudit = false;
        var retry = await ExecuteCreateAsync(services, command);
        retry.Disposition.Should().Be(AtomicCommandDisposition.Executed);
    }

    private async Task<RecurringGraph> SeedGraphAsync(string name)
    {
        var now = DateTime.UtcNow.AddMinutes(-2);
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"recurring-atomic-source:{Guid.NewGuid():N}",
            RendererKey = "recurring-atomic-test",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var property = NewProperty($"Recurring Atomic {name}", now);
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1",
            MarketRent = 1_000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"RECURRING-ATOMIC-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var tenant = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            AccountNumber = $"TA-RECURRING-ATOMIC-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = NewAgreement(management, source, "primary", now);

        var decoyProperty = NewProperty($"Recurring Atomic {name} decoy", now);
        var decoyUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            UnitNumber = "1",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherManagement = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            Unit = decoyUnit,
            RelationshipNumber = $"RECURRING-ATOMIC-OTHER-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var otherAgreement = NewAgreement(otherManagement, source, "other", now);

        var incomeAccount = _setup.Db.LedgerAccounts.Single(row =>
            row.PortfolioId == PortfolioId && row.Code == "4000");
        var expenseAccount = _setup.Db.LedgerAccounts.Single(row =>
            row.PortfolioId == PortfolioId && row.Code == "5000");

        _setup.Db.AddRange(
            source,
            property,
            unit,
            management,
            tenant,
            agreement,
            decoyProperty,
            decoyUnit,
            otherManagement,
            otherAgreement);
        await _setup.Db.SaveChangesAsync();

        return new RecurringGraph(
            tenant.Id,
            agreement.Id,
            otherAgreement.Id,
            property.Id,
            unit.Id,
            decoyProperty.Id,
            incomeAccount.Id,
            expenseAccount.Id);
    }

    private static Property NewProperty(string name, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Name = name,
        AddressLine1 = "1 Recurring Atomic Lane",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseAgreement NewAgreement(
        LeaseManagement management,
        LegalDocumentSourceVersion source,
        string suffix,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        LeaseManagement = management,
        VersionNumber = 1,
        AgreementNumber = $"AGR-RECURRING-ATOMIC-{suffix}-{Guid.NewGuid():N}",
        ChangeType = LeaseAgreementChangeType.Initial,
        TermType = LeaseAgreementTermType.FixedTerm,
        TermStartOn = new DateOnly(2027, 1, 1),
        TermEndOn = new DateOnly(2027, 12, 31),
        GoverningFromOn = new DateOnly(2027, 1, 1),
        BaseRentAmount = 1_000m,
        RentDueDay = 1,
        SecurityDepositObligation = 0m,
        LateFeeAmount = 0m,
        GracePeriodDays = 5,
        Currency = "USD",
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersion = source,
        CreatedAtUtc = now,
        CreatedByUserId = 1,
        UpdatedAtUtc = now,
    };

    private CreateRecurringTenantChargeCommand CreateCommand(
        RecurringGraph graph,
        WorkspaceReadScope scope,
        string key,
        int? ledgerAccountId = null,
        int? leaseAgreementId = null) => new(
        PortfolioId,
        Actor(scope),
        graph.TenantAccountId,
        leaseAgreementId ?? graph.LeaseAgreementId,
        "Monthly pet rent",
        275m,
        ledgerAccountId ?? graph.IncomeAccountId,
        new DateOnly(2027, 1, 1),
        null,
        31,
        new DateOnly(2027, 1, 31),
        DateTime.UtcNow,
        key);

    private static StaffOperationActor Actor(WorkspaceReadScope scope) =>
        new(scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private async Task<AtomicCommandOutcome<RecurringTenantChargeMutationResult>> Execute<TCommand>(
        string _,
        string idempotencyKey,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        if (command is CreateRecurringTenantChargeCommand create)
        {
            var handler = new CreateRecurringTenantChargeRule(db);
            return await writes.ExecuteAsync(idempotencyKey,
                TenantMoneyWriteSupport.Write(create, handler.ExecuteAsync, handler.AuthorizeAsync));
        }
        if (command is UpdateRecurringTenantChargeCommand update)
        {
            var handler = new UpdateRecurringTenantChargeRule(db);
            return await writes.ExecuteAsync(idempotencyKey,
                TenantMoneyWriteSupport.Write(update, handler.ExecuteAsync, handler.AuthorizeAsync));
        }
        if (command is DeactivateRecurringTenantChargeCommand deactivate)
        {
            var handler = new DeactivateRecurringTenantChargeRule(db);
            return await writes.ExecuteAsync(idempotencyKey,
                TenantMoneyWriteSupport.Write(deactivate, handler.ExecuteAsync, handler.AuthorizeAsync));
        }
        throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static async Task<AtomicCommandOutcome<RecurringTenantChargeMutationResult>>
        ExecuteCreateAsync(IServiceProvider services, CreateRecurringTenantChargeCommand command)
    {
        await using var scope = services.CreateAsyncScope();
        var handler = new CreateRecurringTenantChargeRule(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(command.DeliveryIdempotencyKey, TenantMoneyWriteSupport.Write(
                command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private static ServiceProvider CreateServices(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
        });
        return services.BuildServiceProvider();
    }

    private sealed record RecurringGraph(
        int TenantAccountId,
        int LeaseAgreementId,
        int OtherLeaseAgreementId,
        int PropertyId,
        int UnitId,
        int DecoyPropertyId,
        int IncomeAccountId,
        int ExpenseAccountId);

    private sealed class AuditInsertFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfConfigured(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfConfigured(DbCommand command)
        {
            if (FailAtomicAudit && command.CommandText.Contains(
                    "INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected recurring-charge audit failure.");
        }
    }
}
