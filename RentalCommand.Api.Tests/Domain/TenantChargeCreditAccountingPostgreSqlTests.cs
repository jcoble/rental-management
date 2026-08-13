using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Time;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class TenantChargeCreditAccountingPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SimulatedEntryAtUtc =
        new(2027, 01, 05, 14, 30, 00, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<TenantChargeMutationResult> ChargeCodec =
        new("tenant-account.charge.mutation.v1");
    private static readonly AtomicJsonResultCodec<TenantLedgerMutationResult> CreditCodec =
        new("tenant-account.credit.mutation.v1");
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private IServiceScope _serviceScope = null!;
    private IAtomicUnitOfWork _atomic = null!;
    private WorkspaceReadScope _scope;

    public TenantChargeCreditAccountingPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(_ctx.Db).SeedAsync(PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        _scope = _ctx.Db.SeedAdministratorScope(
            PortfolioId, nameof(TenantChargeCreditAccountingPostgreSqlTests));
        await FreezeSimulationClockAsync();
        _services = BuildServices(_ctx.ConnectionString, SimulatedEntryAtUtc);
        _serviceScope = _services.CreateScope();
        _atomic = _serviceScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
    }

    public async Task DisposeAsync()
    {
        _serviceScope.Dispose();
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task TenantChargeCredit_UtilityChargePostsSelectedIncomeAccount()
    {
        var graph = SeedTenantAccount("utility-charge", 100m);
        var utilityIncomeId = await IncomeAccountIdAsync("utility-reimbursement-income");

        var charge = await ExecuteChargeAsync(ChargeCommand(
            graph.AccountId, "utility-charge", 100m, utilityIncomeId,
            new DateOnly(2027, 01, 01), new DateOnly(2027, 01, 31)));

        var entry = await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.Id == charge.LedgerEntryId);
        entry.ServicePeriodStartOn.Should().Be(new DateOnly(2027, 01, 01));
        entry.ServicePeriodEndOn.Should().Be(new DateOnly(2027, 01, 31));

        var lines = await JournalLinesAsync(JournalSourceType.TenantCharge, charge.LedgerEntryId);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "tenant-accounts-receivable"
            && line.DebitAmount == 100m
            && line.CreditAmount == 0m);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "utility-reimbursement-income"
            && line.DebitAmount == 0m
            && line.CreditAmount == 100m);
    }

    [Fact]
    public async Task TenantLedgerPeriodSummary_ValidEmptyAccountReturnsZeroSummary()
    {
        var graph = SeedTenantAccount("empty-period-summary");

        var summary = await new AccountingLedgerReadModelService(_ctx.Db)
            .GetTenantLedgerPeriodSummaryAsync(PortfolioId, graph.AccountId, 12);

        summary.Should().NotBeNull();
        summary!.Currency.Should().Be("USD");
        summary.EndingBalance.Should().Be(0m);
        summary.ChargeAmount.Should().Be(0m);
        summary.PaymentAmount.Should().Be(0m);
        summary.CreditAmount.Should().Be(0m);
    }

    [Fact]
    public async Task TenantChargeCredit_TargetedCreditPostsOriginalIncomeAgainstReceivable()
    {
        var graph = SeedTenantAccount("targeted-income", 100m);
        var utilityIncomeId = await IncomeAccountIdAsync("utility-reimbursement-income");
        var charge = await ExecuteChargeAsync(ChargeCommand(
            graph.AccountId, "targeted-income", 100m, utilityIncomeId));

        var credit = await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "targeted-income", 40m, charge.LedgerEntryId,
            incomeLedgerAccountId: await AccountIdAsync("utilities")));

        var lines = await JournalLinesAsync(JournalSourceType.TenantConcession, credit.LedgerEntryId);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "utility-reimbursement-income"
            && line.DebitAmount == 40m
            && line.CreditAmount == 0m);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "tenant-accounts-receivable"
            && line.DebitAmount == 0m
            && line.CreditAmount == 40m);
    }

    [Fact]
    public async Task TenantChargeCredit_TargetedCreditReducesOpenChargeFrom100To60()
    {
        var graph = SeedTenantAccount("running-balance", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "running-balance", 100m));
        await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "running-balance", 40m, charge.LedgerEntryId));

        var openAmount = await _ctx.Db.TenantChargeBalanceProjections.AsNoTracking()
            .Where(row => row.PortfolioId == PortfolioId
                && row.TenantAccountId == graph.AccountId
                && row.TenantLedgerEntryId == charge.LedgerEntryId)
            .Select(row => row.OpenAmount)
            .SingleAsync();

        openAmount.Should().Be(60m);
    }

    [Fact]
    public async Task TenantChargeCredit_TargetedCreditDoesNotChangeCashBalancesOrFlow()
    {
        var graph = SeedTenantAccount("cash-neutral", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "cash-neutral", 100m));
        var before = await CashTotalsAsync();

        await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "cash-neutral", 40m, charge.LedgerEntryId));

        var after = await CashTotalsAsync();
        after.Should().Be(before);
    }

    [Fact]
    public async Task TenantChargeCredit_FullyAllocatedTargetRejectsCreditBeforeWrites()
    {
        var graph = SeedTenantAccount("fully-paid");
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "fully-paid", 100m));
        var receipt = ReceiptCommand(graph.AccountId, charge.LedgerEntryId, 100m, "fully-paid") with
        {
            AllocateOldestCharges = true,
        };
        await ExecuteReceiptAsync(receipt);

        await AssertCreditRejectedBeforeWritesAsync(CreditCommand(
            graph.AccountId, "fully-paid", 40m, charge.LedgerEntryId));
    }

    [Fact]
    public async Task TenantChargeCredit_TargetedCreditPersistsRelationshipToOriginalCharge()
    {
        var graph = SeedTenantAccount("relationship", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "relationship", 100m));
        var credit = await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "relationship", 40m, charge.LedgerEntryId));

        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .Where(row => row.Id == credit.LedgerEntryId)
            .Select(row => row.RelatedTenantLedgerEntryId)
            .SingleAsync()).Should().Be(charge.LedgerEntryId);
    }

    [Fact]
    public async Task TenantChargeCredit_LateFeeCreditUsesLateFeeIncome()
    {
        var graph = SeedTenantAccount("late-fee", 100m);
        var lateFeeIncomeId = await IncomeAccountIdAsync("late-fee-income");
        var charge = await ExecuteChargeAsync(ChargeCommand(
            graph.AccountId, "late-fee", 100m, lateFeeIncomeId));
        var credit = await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "late-fee", 40m, charge.LedgerEntryId));

        var lines = await JournalLinesAsync(JournalSourceType.TenantConcession, credit.LedgerEntryId);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "late-fee-income"
            && line.DebitAmount == 40m
            && line.CreditAmount == 0m);
        lines.Should().NotContain(line => line.SystemKey == "rental-income");
    }

    [Fact]
    public async Task TenantChargeCredit_GenericCreditCannotTargetDepositCharge()
    {
        var graph = SeedTenantAccount(
            "deposit-target", 100m, TenantLedgerEntryType.DepositCharge);
        var command = CreditCommand(
            graph.AccountId, "deposit-target", 40m, graph.ChargeEntryId);

        await FluentActions.Invoking(() => _atomic.ExecuteAsync(
                new AtomicCommandIdentity("tenant-account.credit.post", command.DeliveryIdempotencyKey),
                command,
                CreditCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*security-deposit*");

        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(row => row.BusinessKey == command.BusinessKey)).Should().Be(0);
        (await _ctx.Db.JournalEntries.AsNoTracking()
            .CountAsync(row => row.SourceBusinessKey == command.BusinessKey)).Should().Be(0);
    }

    [Fact]
    public async Task TenantChargeCredit_ExactReplayDoesNotDuplicateLedgerAllocationOrJournal()
    {
        var graph = SeedTenantAccount("replay", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "replay", 100m));
        var command = CreditCommand(graph.AccountId, "replay", 40m, charge.LedgerEntryId);
        var first = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.credit.post", command.DeliveryIdempotencyKey),
            command,
            CreditCodec);

        var countsBefore = await MutationCountsAsync(command.BusinessKey);
        var replay = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.credit.post", command.DeliveryIdempotencyKey),
            command,
            CreditCodec);
        var countsAfter = await MutationCountsAsync(command.BusinessKey);

        replay.Value.Should().BeEquivalentTo(first.Value);
        countsAfter.Should().Be(countsBefore);
    }

    [Fact]
    public async Task TenantChargeCredit_SelectedAndDerivedIncomeJournalsAreBalanced()
    {
        var graph = SeedTenantAccount("generic-income", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "generic-income", 100m));
        var utilityIncomeId = await IncomeAccountIdAsync("utility-reimbursement-income");
        var credit = await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "generic-income", 25m, incomeLedgerAccountId: utilityIncomeId));

        var totals = await JournalTotalsAsync(JournalSourceType.TenantConcession, credit.LedgerEntryId);
        totals.Debits.Should().Be(25m);
        totals.Credits.Should().Be(25m);

        var lines = await JournalLinesAsync(JournalSourceType.TenantConcession, credit.LedgerEntryId);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "utility-reimbursement-income"
            && line.DebitAmount == 25m);
        lines.Should().NotContain(line => line.SystemKey == "rental-income");
        charge.LedgerEntryId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TenantChargeCredit_TargetedCreditCannotExceedOriginalChargeAfterCorrections()
    {
        var graph = SeedTenantAccount("over-credit", 100m);
        var charge = await ExecuteChargeAsync(ChargeCommand(graph.AccountId, "over-credit", 100m));
        await ExecuteCreditAsync(CreditCommand(
            graph.AccountId, "over-credit-first", 60m, charge.LedgerEntryId));
        var command = CreditCommand(graph.AccountId, "over-credit-second", 50m, charge.LedgerEntryId);

        await FluentActions.Invoking(() => _atomic.ExecuteAsync(
                new AtomicCommandIdentity("tenant-account.credit.post", command.DeliveryIdempotencyKey),
                command,
                CreditCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*remaining amount*");

        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(row => row.BusinessKey == command.BusinessKey)).Should().Be(0);
    }

    [Fact]
    public async Task TargetedCreditEligibility_FutureCreditAgreesAcrossCandidatesLedgerAndCommitAtBusinessDateBoundary()
    {
        var graph = SeedTenantAccount("eligibility-boundary");
        var charge = await ExecuteChargeAsync(ChargeCommand(
            graph.AccountId, "eligibility-boundary", 100m));
        var futureCreditCommand = CreditCommand(
            graph.AccountId, "eligibility-boundary-future", 100m, charge.LedgerEntryId,
            effectiveOn: new DateOnly(2027, 01, 06));
        var futureCredit = await ExecuteCreditAsync(futureCreditCommand);

        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(row => row.Id == futureCredit.LedgerEntryId
                && row.BusinessKey == futureCreditCommand.BusinessKey
                && row.RelatedTenantLedgerEntryId == charge.LedgerEntryId)).Should().Be(1);
        (await _ctx.Db.JournalEntries.AsNoTracking()
            .CountAsync(row => row.SourceType == JournalSourceType.TenantConcession
                && row.SourceId == futureCredit.LedgerEntryId
                && row.SourceBusinessKey == futureCreditCommand.BusinessKey)).Should().Be(1);
        (await _ctx.Db.TenantLedgerAllocations.AsNoTracking()
            .CountAsync(row => row.DebitEntryId == charge.LedgerEntryId
                && row.CreditEntryId == futureCredit.LedgerEntryId
                && row.Amount == 100m)).Should().Be(1);
        await _ctx.ActivateApiScopeAsync(_scope);

        var readModel = new AccountingLedgerReadModelService(_ctx.Db);
        var candidatesBefore = await readModel.GetTenantCreditTargetsAsync(
            _scope, graph.AccountId, new TenantCreditTargetQuery { Take = 20 });
        var ledgerBefore = await readModel.GetTenantLedgerAsync(
            _scope, graph.AccountId, new TenantLedgerQuery { Take = 20 });

        candidatesBefore!.Items.Should().NotContain(row =>
            row.TenantLedgerEntryId == charge.LedgerEntryId);
        ledgerBefore!.Items.Single(row => row.TenantLedgerEntryId == charge.LedgerEntryId)
            .ActionCapabilities.CanGiveCredit.Should().BeFalse();
        var beforeBoundaryCommand = CreditCommand(
            graph.AccountId, "eligibility-boundary-before", 1m, charge.LedgerEntryId);
        await AssertCreditRejectedBeforeWritesAsync(beforeBoundaryCommand);

        await _ctx.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
        await FreezeSimulationClockAsync(new DateTime(2027, 01, 06, 14, 30, 00, DateTimeKind.Utc));
        await _ctx.ActivateApiScopeAsync(_scope);

        var candidatesOnBoundary = await readModel.GetTenantCreditTargetsAsync(
            _scope, graph.AccountId, new TenantCreditTargetQuery { Take = 20 });
        var ledgerOnBoundary = await readModel.GetTenantLedgerAsync(
            _scope, graph.AccountId, new TenantLedgerQuery { Take = 20 });
        var afterBoundaryCommand = CreditCommand(
            graph.AccountId, "eligibility-boundary-on", 1m, charge.LedgerEntryId);

        candidatesOnBoundary!.Items.Should().NotContain(row =>
            row.TenantLedgerEntryId == charge.LedgerEntryId);
        ledgerOnBoundary!.Items.Single(row => row.TenantLedgerEntryId == charge.LedgerEntryId)
            .ActionCapabilities.CanGiveCredit.Should().BeFalse();
        await AssertCreditRejectedBeforeWritesAsync(afterBoundaryCommand);
    }

    private async Task<TenantChargeMutationResult> ExecuteChargeAsync(PostTenantChargeCommand command)
    {
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", command.DeliveryIdempotencyKey),
            command,
            ChargeCodec);
        return outcome.Value;
    }

    private async Task<TenantLedgerMutationResult> ExecuteCreditAsync(PostTenantCreditCommand command)
    {
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.credit.post", command.DeliveryIdempotencyKey),
            command,
            CreditCodec);
        return outcome.Value;
    }

    private async Task AssertCreditRejectedBeforeWritesAsync(PostTenantCreditCommand command)
    {
        await FluentActions.Invoking(() => _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "tenant-account.credit.post", command.DeliveryIdempotencyKey),
                command,
                CreditCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*remaining amount*");

        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(row => row.BusinessKey == command.BusinessKey)).Should().Be(0);
        (await _ctx.Db.JournalEntries.AsNoTracking()
            .CountAsync(row => row.SourceBusinessKey == command.BusinessKey)).Should().Be(0);
        (await _ctx.Db.TenantLedgerAllocations.AsNoTracking()
            .CountAsync(row => row.BusinessKey.StartsWith(command.BusinessKey))).Should().Be(0);
    }

    private async Task<RecordTenantReceiptResult> ExecuteReceiptAsync(RecordTenantReceiptCommand command)
    {
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);
        return outcome.Value;
    }

    private PostTenantChargeCommand ChargeCommand(
        int accountId,
        string suffix,
        decimal amount,
        int? incomeLedgerAccountId = null,
        DateOnly? servicePeriodStartOn = null,
        DateOnly? servicePeriodEndOn = null) => new(
        PortfolioId,
        accountId,
        amount,
        new DateOnly(2027, 01, 05),
        new DateOnly(2027, 01, 05),
        $"{suffix} charge",
        null,
        _scope.UserId,
        _scope.SessionId,
        _scope.AccessContextId,
        _scope.AccessRevision,
        CapabilityKeys.MoneyChargesManage,
        $"tenant-charge-{suffix}",
        $"tenant-charge:{PortfolioId}:{accountId}:{suffix}",
        incomeLedgerAccountId,
        servicePeriodStartOn,
        servicePeriodEndOn);

    private PostTenantCreditCommand CreditCommand(
        int accountId,
        string suffix,
        decimal amount,
        long? targetChargeEntryId = null,
        int? incomeLedgerAccountId = null,
        bool allocateOldestCharges = false,
        DateOnly? effectiveOn = null) => new(
        PortfolioId,
        accountId,
        amount,
        effectiveOn ?? new DateOnly(2027, 01, 05),
        $"{suffix} credit",
        null,
        allocateOldestCharges,
        _scope.UserId,
        _scope.SessionId,
        _scope.AccessContextId,
        _scope.AccessRevision,
        CapabilityKeys.MoneyChargesManage,
        $"tenant-credit-{suffix}",
        $"tenant-credit:{PortfolioId}:{accountId}:{suffix}",
        targetChargeEntryId,
        incomeLedgerAccountId);

    private RecordTenantReceiptCommand ReceiptCommand(
        int accountId,
        long targetChargeEntryId,
        decimal amount,
        string suffix) => new(
        PortfolioId,
        accountId,
        amount,
        new DateOnly(2027, 01, 05),
        $"{suffix} receipt",
        "Bank transfer",
        $"PMT-{suffix}",
        "Receipt Tenant",
        null,
        null,
        null,
        targetChargeEntryId,
        _scope.UserId,
        _scope.SessionId,
        _scope.AccessContextId,
        _scope.AccessRevision,
        CapabilityKeys.MoneyPaymentsManage,
        $"tenant-receipt-{suffix}",
        $"tenant-receipt:{PortfolioId}:{accountId}:{suffix}");

    private TenantAccountGraph SeedTenantAccount(
        string suffix,
        decimal? seededChargeAmount = null,
        TenantLedgerEntryType seededChargeType = TenantLedgerEntryType.ManualCharge)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Charge credit property {suffix}",
            AddressLine1 = "100 Accounting Street",
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
            MarketRent = seededChargeAmount ?? 100m,
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
        TenantLedgerEntry? charge = null;
        if (seededChargeAmount is decimal amount)
        {
            charge = new TenantLedgerEntry
            {
                PortfolioId = PortfolioId,
                TenantAccount = account,
                EntryType = seededChargeType,
                Direction = TenantLedgerDirection.Debit,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 01, 05),
                DueOn = new DateOnly(2027, 01, 05),
                PostedAtUtc = seededAt,
                Description = $"{suffix} seeded charge",
                BusinessKey = $"seeded-charge-{suffix}",
                CreatedByUserId = _scope.UserId,
            };
        }

        _ctx.Db.AddRange(property, unit, relationship, account);
        if (charge is not null)
            _ctx.Db.Add(charge);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantAccountGraph(account.Id, charge?.Id);
    }

    private async Task<int> IncomeAccountIdAsync(string systemKey) =>
        await _ctx.Db.LedgerAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == PortfolioId
                && account.SystemKey == systemKey
                && account.AccountType == AccountType.Income)
            .Select(account => account.Id)
            .SingleAsync();

    private async Task<int> AccountIdAsync(string systemKey) =>
        await _ctx.Db.LedgerAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == PortfolioId && account.SystemKey == systemKey)
            .Select(account => account.Id)
            .SingleAsync();

    private async Task<IReadOnlyList<JournalLineSnapshot>> JournalLinesAsync(
        JournalSourceType sourceType, long sourceId) =>
        await (
            from journal in _ctx.Db.JournalEntries.AsNoTracking()
            from line in journal.Lines
            join account in _ctx.Db.LedgerAccounts.AsNoTracking()
                on line.LedgerAccountId equals account.Id
            where journal.PortfolioId == PortfolioId
                && journal.SourceType == sourceType
                && journal.SourceId == sourceId
            select new JournalLineSnapshot(
                account.SystemKey, line.DebitAmount, line.CreditAmount))
            .ToListAsync();

    private async Task<JournalTotals> JournalTotalsAsync(JournalSourceType sourceType, long sourceId)
    {
        var totals = await (
            from journal in _ctx.Db.JournalEntries.AsNoTracking()
            from line in journal.Lines
            where journal.PortfolioId == PortfolioId
                && journal.SourceType == sourceType
                && journal.SourceId == sourceId
            group line by 1
            into lines
            select new
            {
                Debits = lines.Sum(line => line.DebitAmount),
                Credits = lines.Sum(line => line.CreditAmount),
            }).SingleAsync();
        return new JournalTotals(totals.Debits, totals.Credits);
    }

    private async Task<CashTotals> CashTotalsAsync()
    {
        var cashKeys = new[]
        {
            "operating-cash",
            "undeposited-funds",
            "security-deposit-trust-cash",
        };
        var totals = await (
            from line in _ctx.Db.JournalLines.AsNoTracking()
            join account in _ctx.Db.LedgerAccounts.AsNoTracking()
                on line.LedgerAccountId equals account.Id
            where account.PortfolioId == PortfolioId
                && account.SystemKey != null
                && cashKeys.Contains(account.SystemKey)
            group line by 1
            into lines
            select new
            {
                Debits = lines.Sum(line => line.DebitAmount),
                Credits = lines.Sum(line => line.CreditAmount),
            }).SingleOrDefaultAsync();
        return new CashTotals(totals?.Debits ?? 0m, totals?.Credits ?? 0m);
    }

    private async Task<MutationCounts> MutationCountsAsync(string businessKey)
    {
        var ledgerCount = await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(row => row.BusinessKey == businessKey);
        var allocationCount = await _ctx.Db.TenantLedgerAllocations.AsNoTracking()
            .CountAsync(row => row.BusinessKey.StartsWith(businessKey));
        var journalCount = await _ctx.Db.JournalEntries.AsNoTracking()
            .CountAsync(row => row.SourceBusinessKey == businessKey);
        return new MutationCounts(ledgerCount, allocationCount, journalCount);
    }

    private static ServiceProvider BuildServices(string connectionString, DateTime utcNow)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            PostTenantChargeCommand, TenantChargeMutationResult, PostTenantChargeHandler>();
        services.AddAtomicCommandHandler<
            PostTenantCreditCommand, TenantLedgerMutationResult, PostTenantCreditHandler>();
        services.AddAtomicCommandHandler<
            RecordTenantReceiptCommand, RecordTenantReceiptResult, RecordTenantReceiptHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async Task FreezeSimulationClockAsync(DateTime? simulatedNowUtc = null)
    {
        var effectiveNowUtc = simulatedNowUtc ?? SimulatedEntryAtUtc;
        var clock = await _ctx.Db.SimulationClocks.SingleOrDefaultAsync(row => row.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = effectiveNowUtc,
                RealAnchorUtc = DateTime.UtcNow,
                TimeZoneId = "America/New_York",
            });
        }
        else
        {
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = effectiveNowUtc;
            clock.RealAnchorUtc = DateTime.UtcNow;
            clock.TimeZoneId = "America/New_York";
        }

        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
    }

    private sealed record TenantAccountGraph(int AccountId, long? ChargeEntryId);
    private sealed record JournalLineSnapshot(string? SystemKey, decimal DebitAmount, decimal CreditAmount);
    private sealed record JournalTotals(decimal Debits, decimal Credits);
    private sealed record CashTotals(decimal Debits, decimal Credits);
    private sealed record MutationCounts(int LedgerCount, int AllocationCount, int JournalCount);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
