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
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
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
        DateTime recordedAtUtc) => new(
            PortfolioId,
            accountId,
            amount,
            new DateOnly(2027, 01, 05),
            "January 2027 rent receipt",
            "Bank transfer",
            $"PMT-{suffix}",
            "Receipt Tenant",
            null,
            null,
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

    private ConfirmScanDraftCommand ScanCommand(
        ScanDraft draft,
        int accountId,
        decimal amount,
        DateTime confirmedAtUtc)
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
                    Receipt(amount, confirmedAtUtc),
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

    private static ScanReceiptData Receipt(decimal amount, DateTime transactionAtUtc) => new(
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
        CheckNumber: "1001",
        BankName: "Test Bank",
        ExtraFields: []);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed record TenantAccountGraph(int AccountId);
}
