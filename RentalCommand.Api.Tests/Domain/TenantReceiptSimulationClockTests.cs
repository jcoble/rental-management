using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Scanning;
using RentalCommand.TestCommon;
using RentalCommand.Api.Tests.Scanning;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class TenantReceiptSimulationClockTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SimulatedEntryAtUtc =
        new(2027, 01, 05, 14, 30, 00, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<ConfirmScanDraftResult> ScanCodec =
        new("scan-confirm.result.v1");
    private static readonly AtomicJsonResultCodec<SecurityDepositMutationResult> DepositCodec =
        new("tenant-account.security-deposit.mutation.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private IAtomicUnitOfWork _atomic = null!;
    private WorkspaceReadScope _scope;

    public TenantReceiptSimulationClockTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(TenantReceiptSimulationClockTests));
        await FreezeSimulationClockAsync();
        _services = BuildServices(_ctx.ConnectionString, SimulatedEntryAtUtc);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task ManualReceipt_UsesCommandSimulationClockForAttemptLedgerAllocationAndOutbox()
    {
        var graph = SeedTenantAccountWithOpenCharge("manual-sim-clock", 1_200m);
        var command = ReceiptCommand(graph.AccountId, 1_200m, "manual-sim-clock", SimulatedEntryAtUtc);

        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        outcome.Value.AllocatedAmount.Should().Be(1_200m);
        outcome.Value.AllocationCount.Should().Be(1);
        _ctx.Db.ChangeTracker.Clear();
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        var row = await (
            from entry in _ctx.Db.TenantLedgerEntries.AsNoTracking()
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on entry.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on entry.Id equals allocation.CreditEntryId
            join outbox in _ctx.Db.OutboxMessages.AsNoTracking()
                on entry.PortfolioId equals outbox.PortfolioId
            where entry.Id == outcome.Value.LedgerEntryId
                && outbox.IdempotencyKey == outboxKey
            select new
            {
                entry.PostedAtUtc,
                attempt.PreparedAtUtc,
                attempt.SubmittedAtUtc,
                attempt.SettledAtUtc,
                attempt.UpdatedAtUtc,
                allocation.AllocatedAtUtc,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
            }).SingleAsync();

        row.PostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SubmittedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SettledAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.UpdatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxCreatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxNextAttemptAtUtc.Should().Be(SimulatedEntryAtUtc);
    }

    [Fact]
    public async Task ScanPaymentConfirmation_CarriesConfirmedSimulationClockIntoReceiptMutation()
    {
        var graph = SeedTenantAccountWithOpenCharge("scan-sim-clock", 1_875m);
        var draft = SeedPaymentScanDraft(graph.AccountId);
        var command = ScanCommand(draft, graph.AccountId, 1_875m, SimulatedEntryAtUtc);

        var outcome = await _atomic.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(PortfolioId, draft.Id, "scan-sim-clock"),
            command,
            ScanCodec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        var ledgerEntryId = outcome.Value.LedgerEntryId!.Value;
        ledgerEntryId.Should().BePositive();
        _ctx.Db.ChangeTracker.Clear();
        var row = await (
            from entry in _ctx.Db.TenantLedgerEntries.AsNoTracking()
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on entry.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on entry.Id equals allocation.CreditEntryId
            where entry.Id == ledgerEntryId
            select new
            {
                entry.PostedAtUtc,
                attempt.PreparedAtUtc,
                attempt.SettledAtUtc,
                allocation.AllocatedAtUtc,
            }).SingleAsync();

        row.PostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SettledAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
    }

    [Fact]
    public async Task ScanPaymentConfirmation_AllowsSameManualCheckNumberOnDifferentTenantAccounts()
    {
        const string repeatedCheckNumber = "000424242";
        var existingGraph = SeedTenantAccountWithOpenCharge("same-check-existing", 1_675m);
        var scanGraph = SeedTenantAccountWithOpenCharge("same-check-scan", 1_350m);
        var existingCommand = ReceiptCommand(
            existingGraph.AccountId,
            1_675m,
            "same-check-existing",
            SimulatedEntryAtUtc.AddMinutes(-5),
            repeatedCheckNumber,
            repeatedCheckNumber);

        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", existingCommand.DeliveryIdempotencyKey),
            existingCommand,
            ReceiptCodec);
        var draft = SeedPaymentScanDraft(scanGraph.AccountId);
        var scanCommand = ScanCommand(
            draft,
            scanGraph.AccountId,
            1_350m,
            SimulatedEntryAtUtc,
            repeatedCheckNumber);

        var outcome = await _atomic.ExecuteAsync(
            ScanConfirmationCommandIdentity.Create(PortfolioId, draft.Id, "scan-same-check"),
            scanCommand,
            ScanCodec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        _ctx.Db.ChangeTracker.Clear();
        var attempts = await _ctx.Db.TenantPaymentAttempts.AsNoTracking()
            .Where(attempt => attempt.Provider == "manual"
                && attempt.ProviderObjectId == repeatedCheckNumber)
            .Select(attempt => new
            {
                attempt.TenantAccountId,
                attempt.Amount,
                attempt.CheckNumber,
            })
            .OrderBy(attempt => attempt.TenantAccountId)
            .ToListAsync();

        attempts.Should().HaveCount(2);
        attempts.Select(attempt => attempt.TenantAccountId)
            .Should().BeEquivalentTo([existingGraph.AccountId, scanGraph.AccountId]);
        attempts.Should().OnlyContain(attempt => attempt.CheckNumber == repeatedCheckNumber);
    }

    [Fact]
    public async Task ReceiptMutation_RollsBackPaymentAttemptWhenLaterLedgerInsertFails()
    {
        var graph = SeedTenantAccountWithOpenCharge("receipt-rollback", 1_200m);
        var duplicateBusinessKey = "receipt-rollback-duplicate";
        _ctx.Db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.AccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 05),
            PostedAtUtc = SimulatedEntryAtUtc.AddMinutes(-5),
            Description = "Conflicting receipt",
            BusinessKey = duplicateBusinessKey,
            CreatedByUserId = _scope.UserId,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var command = ReceiptCommand(graph.AccountId, 1_200m, "receipt-rollback", SimulatedEntryAtUtc)
            with { BusinessKey = duplicateBusinessKey };

        Func<Task> act = async () => await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        await act.Should().ThrowAsync<DbUpdateException>();
        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantPaymentAttempts.AsNoTracking()
            .CountAsync(attempt => attempt.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(receipt => receipt.CommandType == "tenant-account.receipt.record"
                && receipt.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(0);
    }

    [Fact]
    public async Task DepositFunding_UsesSimulationClockForBusinessRowsButWallClockForManualAttempt()
    {
        var graph = SeedTenantAccountWithOpenDepositCharge("deposit-sim-clock", 1_675m);
        var command = FundDepositCommand(graph, 1_675m, "deposit-sim-clock");

        var beforeWallClock = DateTime.UtcNow.AddSeconds(-5);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.deposit.fund", command.DeliveryIdempotencyKey),
            command,
            DepositCodec);
        var afterWallClock = DateTime.UtcNow.AddSeconds(5);

        outcome.Value.Applied.Should().BeTrue();
        _ctx.Db.ChangeTracker.Clear();
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        var row = await (
            from deposit in _ctx.Db.SecurityDepositEntries.AsNoTracking()
            join receipt in _ctx.Db.TenantLedgerEntries.AsNoTracking()
                on deposit.TenantLedgerEntryId equals receipt.Id
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on receipt.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on receipt.Id equals allocation.CreditEntryId
            join audit in _ctx.Db.AtomicAuditLogs.AsNoTracking()
                on receipt.TenantAccountId equals audit.EntityId
            join outbox in _ctx.Db.OutboxMessages.AsNoTracking()
                on receipt.PortfolioId equals outbox.PortfolioId
            where deposit.Id == outcome.Value.SecurityDepositEntryId
                && audit.CommandType == "tenant-account.deposit.fund"
                && audit.EntityType == nameof(TenantAccount)
                && outbox.IdempotencyKey == outboxKey
            select new
            {
                ReceiptPostedAtUtc = receipt.PostedAtUtc,
                AllocationAllocatedAtUtc = allocation.AllocatedAtUtc,
                DepositPostedAtUtc = deposit.PostedAtUtc,
                AuditTimestamp = audit.Timestamp,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
                attempt.PreparedAtUtc,
                attempt.SubmittedAtUtc,
                attempt.SettledAtUtc,
                attempt.UpdatedAtUtc,
            }).SingleAsync();

        row.ReceiptPostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocationAllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.DepositPostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AuditTimestamp.Should().Be(SimulatedEntryAtUtc);
        row.OutboxCreatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxNextAttemptAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().BeOnOrAfter(beforeWallClock);
        row.PreparedAtUtc.Should().BeOnOrBefore(afterWallClock);
        row.SubmittedAtUtc.Should().Be(row.PreparedAtUtc);
        row.SettledAtUtc.Should().Be(row.PreparedAtUtc);
        row.UpdatedAtUtc.Should().Be(row.PreparedAtUtc);
    }

    private static ServiceProvider BuildServices(string connectionString, DateTime utcNow)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddScoped<ProductionScanConfirmationTargetWriter>();
        services.AddAtomicCommandHandler<
            RecordTenantReceiptCommand,
            RecordTenantReceiptResult,
            RecordTenantReceiptHandler>();
        services.AddAtomicCommandHandler<
            ConfirmScanDraftCommand,
            ConfirmScanDraftResult,
            ConfirmScanDraftHandler<ProductionScanConfirmationTargetWriter>>();
        services.AddAtomicCommandHandler<
            FundSecurityDepositCommand,
            SecurityDepositMutationResult,
            FundSecurityDepositHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private async Task FreezeSimulationClockAsync()
    {
        var clock = await _ctx.Db.SimulationClocks.SingleOrDefaultAsync(clock => clock.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = SimulatedEntryAtUtc,
                RealAnchorUtc = DateTime.UtcNow,
                TimeZoneId = "America/New_York",
            });
        }
        else
        {
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = SimulatedEntryAtUtc;
            clock.RealAnchorUtc = DateTime.UtcNow;
            clock.TimeZoneId = "America/New_York";
        }

        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
    }

    private TenantAccountGraph SeedTenantAccountWithOpenCharge(string suffix, decimal amount)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Receipt Test Property {suffix}",
            AddressLine1 = "100 Receipt Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = suffix,
            MarketRent = amount,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Receipt",
            LastName = suffix,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        relationship.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        });
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 01),
            DueOn = new DateOnly(2027, 01, 01),
            PostedAtUtc = seededAt,
            Description = "January 2027 test charge",
            BusinessKey = $"charge-{suffix}",
            CreatedByUserId = _scope.UserId,
        };
        _ctx.Db.AddRange(property, unit, tenant, relationship, account, charge);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantAccountGraph(account.Id);
    }

    private TenantDepositGraph SeedTenantAccountWithOpenDepositCharge(string suffix, decimal amount)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Deposit Test Property {suffix}",
            AddressLine1 = "200 Deposit Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = suffix,
            MarketRent = amount,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Deposit",
            LastName = suffix,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"DLM-{suffix}",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        relationship.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        });
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"DTA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var source = LegalDocumentSourceVersionTestData.BuiltIn(
            PortfolioId,
            _scope.UserId,
            seededAt,
            $"deposit-sim-clock-{suffix}");
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"DAGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2027, 01, 01),
            TermEndOn = new DateOnly(2027, 12, 31),
            GoverningFromOn = new DateOnly(2027, 01, 01),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = amount,
            LateFeeAmount = 25m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            OriginatingAgreement = agreement,
            Currency = "USD",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.DepositCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 05),
            DueOn = new DateOnly(2027, 01, 05),
            PostedAtUtc = seededAt,
            Description = "Security deposit due",
            BusinessKey = $"deposit-charge-{suffix}",
            CreatedByUserId = _scope.UserId,
        };
        _ctx.Db.AddRange(property, unit, tenant, relationship, account, source, agreement,
            depositAccount, charge);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantDepositGraph(account.Id, depositAccount.Id);
    }

    private ScanDraft SeedPaymentScanDraft(int tenantAccountId)
    {
        var extractedFields = "{\"document_type\":{\"value\":\"Payment\"}}";
        var draft = new ScanDraft
        {
            PortfolioId = PortfolioId,
            TargetEntityType = "Payment",
            Status = "Reviewing",
            ExtractedFields = extractedFields,
            CreatedAt = SimulatedEntryAtUtc.AddMinutes(-10),
            CaptureAccessContextId = _scope.AccessContextId,
            CaptureAccessRevision = _scope.AccessRevision,
            CaptureTenantAccountId = tenantAccountId,
        };
        _ctx.Db.ScanDrafts.Add(draft);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return draft;
    }

    private RecordTenantReceiptCommand ReceiptCommand(
        int accountId,
        decimal amount,
        string suffix,
        DateTime recordedAtUtc,
        string? externalReference = null,
        string? checkNumber = null,
        string? bankName = null) => new(
            PortfolioId,
            accountId,
            amount,
            new DateOnly(2027, 01, 05),
            "January 2027 rent receipt",
            "Bank transfer",
            externalReference ?? $"PMT-{suffix}",
            "Receipt Tenant",
            checkNumber,
            bankName,
            null,
            AllocateOldestCharges: true,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            CapabilityKeys.MoneyPaymentsManage,
            $"receipt-{suffix}",
            $"tenant-receipt:{PortfolioId}:{accountId}:{suffix}",
            recordedAtUtc);

    private FundSecurityDepositCommand FundDepositCommand(
        TenantDepositGraph graph,
        decimal amount,
        string suffix) => new(
            PortfolioId,
            graph.AccountId,
            graph.DepositAccountId,
            amount,
            new DateOnly(2027, 01, 05),
            "Security deposit received",
            "Money order",
            $"DEP-{suffix}",
            null,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            CapabilityKeys.MoneyDepositsManage,
            $"deposit-fund-{suffix}",
            $"tenant-deposit:{PortfolioId}:{graph.AccountId}:{suffix}");

    private ConfirmScanDraftCommand ScanCommand(
        ScanDraft draft,
        int accountId,
        decimal amount,
        DateTime confirmedAtUtc,
        string checkNumber = "1001")
    {
        var fingerprint = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256,
            draft.CaptureAccessContextId,
            draft.CaptureAccessRevision,
            draft.CapturePropertyId,
            draft.CaptureUnitId,
            draft.CaptureLeaseManagementId,
            draft.CaptureLeaseAgreementId,
            draft.CaptureTenantAccountId,
            draft.CaptureTenantLedgerEntryId,
            draft.CaptureWorkOrderId,
            draft.CaptureApplicationId,
            draft.CaptureRentalListingId,
            draft.SourceLabel);
        return new ConfirmScanDraftCommand(
            PortfolioId,
            draft.Id,
            _scope.UserId,
            confirmedAtUtc,
            fingerprint,
            new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Payment,
                Payment: new ScanPaymentTargetData(
                    Receipt(amount, confirmedAtUtc, checkNumber),
                    accountId)),
            draft.SourceStoredFileId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            $"scan-confirm:{PortfolioId}:{draft.Id}:scan-sim-clock",
            draft.SourceContentSha256,
            draft.SourceLabel,
            new ScanCaptureContextData(
                null,
                draft.CaptureAccessContextId,
                draft.CaptureAccessRevision,
                draft.CapturePropertyId,
                draft.CaptureUnitId,
                draft.CaptureLeaseManagementId,
                draft.CaptureLeaseAgreementId,
                draft.CaptureTenantAccountId,
                draft.CaptureTenantLedgerEntryId,
                draft.CaptureWorkOrderId,
                draft.CaptureApplicationId,
                draft.CaptureRentalListingId,
                draft.SourceLabel));
    }

    private static ScanReceiptData Receipt(
        decimal amount,
        DateTime transactionAtUtc,
        string checkNumber = "1001") => new(
        VendorName: null,
        VendorAddress: null,
        VendorPhone: null,
        VendorWebsite: null,
        VendorTaxId: null,
        ReceiptNumber: null,
        TransactionDate: transactionAtUtc,
        Subtotal: amount,
        Tax: null,
        TaxRate: null,
        Tip: null,
        Discount: null,
        Shipping: null,
        Total: amount,
        PaymentMethod: "Scanned check",
        CardLast4: null,
        Category: null,
        DocumentKind: "Payment",
        Notes: "Scanned January rent check",
        DueDate: null,
        LineItems: [],
        PayerName: "Receipt Tenant",
        CheckNumber: checkNumber,
        BankName: "Test Bank",
        ExtraFields: []);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed record TenantAccountGraph(int AccountId);

    private sealed record TenantDepositGraph(int AccountId, int DepositAccountId);
}
