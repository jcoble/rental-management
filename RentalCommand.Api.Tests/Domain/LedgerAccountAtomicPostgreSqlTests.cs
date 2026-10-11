using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name1)]
public sealed class LedgerAccountAtomicPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime InterceptorNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public LedgerAccountAtomicPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(LedgerAccountAtomicPostgreSqlTests));
        _services = CreateServices(_context.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task Create_DerivesNormalBalance_AssignsNextCode_ReplaysExactly_AndWritesEvidence()
    {
        var command = CreateCommand(
            "coa-create-replay",
            code: " ",
            name: "Laundry Income",
            accountType: AccountType.Income);

        var first = await ExecuteCreateAsync(command);
        first.Value.Account.Should().NotBeNull();
        var account = first.Value.Account!;
        account.Code.Should().Be("4000");
        account.AccountType.Should().Be(AccountType.Income);
        account.NormalBalance.Should().Be(NormalBalance.Credit);
        account.SystemKey.Should().BeNull();
        account.IsSystem.Should().BeFalse();
        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);

        var replay = await ExecuteCreateAsync(command);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts.CountAsync(row => row.PortfolioId == PortfolioId))
            .Should().Be(1);
        var storedAccount = await _context.Db.LedgerAccounts.SingleAsync(row => row.Id == account.Id);
        var audit = await _context.Db.AtomicAuditLogs.SingleAsync(row =>
            row.PortfolioId == PortfolioId
            && row.EntityType == nameof(LedgerAccount)
            && row.EntityId == account.Id
            && row.Operation == AuditLogOperation.Created);
        audit.Timestamp.Should().Be(storedAccount.CreatedAtUtc);
        audit.Timestamp.Should().NotBe(InterceptorNow);
        var outbox = await _context.Db.OutboxMessages.SingleAsync(row =>
            row.IdempotencyKey == "ledger-account-create:coa-create-replay");
        outbox.MessageType.Should().Be("data-update");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("entityType").GetString().Should().Be(nameof(LedgerAccount));
        payload.RootElement.GetProperty("entityId").GetInt32().Should().Be(account.Id);
        payload.RootElement.GetProperty("operation").GetString().Should().Be("create");

        var receipt = await _context.Db.AtomicCommandReceipts.SingleAsync(row =>
            row.CommandType == "accounting.ledger-account.create"
            && row.IdempotencyKey == "coa-create-replay");
        receipt.Status.Should().Be(AtomicCommandReceiptStatus.Completed);
        receipt.ResultJson.Should().Contain("\"AccountType\": \"Income\"");
    }

    [Fact]
    public async Task BlankCodes_UseIncomeAndExpenseRanges_AndSkipExistingCodes()
    {
        var firstIncome = await ExecuteCreateAsync(CreateCommand(
            "coa-range-income-1", code: "", name: "First Income", accountType: AccountType.Income));
        var secondIncome = await ExecuteCreateAsync(CreateCommand(
            "coa-range-income-2", code: "", name: "Second Income", accountType: AccountType.Income));
        firstIncome.Value.Account!.Code.Should().Be("4000");
        secondIncome.Value.Account!.Code.Should().Be("4001");

        await AddDirectAccountAsync(
            code: "5000",
            name: "Existing Expense",
            accountType: AccountType.Expense);
        var expense = await ExecuteCreateAsync(CreateCommand(
            "coa-range-expense", code: "", name: "First New Expense", accountType: AccountType.Expense));
        expense.Value.Account!.Code.Should().Be("5001");
    }

    [Fact]
    public async Task Create_RejectsClientSystemKey_AndNonUserAccountType_WithoutReceiptOrRows()
    {
        var systemKeyCommand = CreateCommand(
            "coa-invalid-system-key",
            code: "4100",
            name: "Client System Account",
            systemKey: "client-supplied-key");
        await FluentActions.Invoking(() => ExecuteCreateAsync(systemKeyCommand))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*System keys*");

        var assetCommand = CreateCommand(
            "coa-invalid-account-type",
            code: "1001",
            name: "Client Asset Account",
            accountType: AccountType.Asset);
        await FluentActions.Invoking(() => ExecuteCreateAsync(assetCommand))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*Income or Expense*");

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts.CountAsync(row => row.PortfolioId == PortfolioId))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == "accounting.ledger-account.create"))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.CountAsync(row => row.PortfolioId == PortfolioId))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.CountAsync(row => row.PortfolioId == PortfolioId))
            .Should().Be(0);
    }

    [Fact]
    public async Task CapabilityAccountDestructiveActions_IsRequired()
    {
        var property = await AddPropertyAsync();
        var limitedScope = _context.Db.SeedPropertyManagerScope(
            PortfolioId,
            property.Id,
            "ledger-account-limited-actor");
        var command = CreateCommand(
            "coa-capability-denied",
            code: "4101",
            name: "Denied Income",
            scope: limitedScope);

        await FluentActions.Invoking(() => ExecuteCreateAsync(command))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*account.destructive-actions*");

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts.CountAsync(row => row.PortfolioId == PortfolioId))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
                row.IdempotencyKey == "coa-capability-denied"))
            .Should().Be(0);
    }

    [Fact]
    public async Task ParentMustBeInTheSamePortfolioAndHaveTheSameAccountType()
    {
        var wrongTypeParent = await AddDirectAccountAsync(
            code: "1000",
            name: "Asset Parent",
            accountType: AccountType.Asset);
        var wrongTypeCommand = CreateCommand(
            "coa-parent-wrong-type",
            code: "4102",
            name: "Income Child",
            parentAccountId: wrongTypeParent.Id);
        await FluentActions.Invoking(() => ExecuteCreateAsync(wrongTypeCommand))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*same portfolio and have the same account type*");

        var otherPortfolio = new Portfolio
        {
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var otherPortfolioParent = new LedgerAccount
        {
            Portfolio = otherPortfolio,
            Code = "4000",
            Name = "Other Portfolio Income",
            AccountType = AccountType.Income,
            NormalBalance = NormalBalance.Credit,
            IsActive = true,
            IsSystem = false,
            PublicId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        _context.Db.LedgerAccounts.Add(otherPortfolioParent);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var crossPortfolioCommand = CreateCommand(
            "coa-parent-cross-portfolio",
            code: "4103",
            name: "Cross Portfolio Child",
            parentAccountId: otherPortfolioParent.Id);
        await FluentActions.Invoking(() => ExecuteCreateAsync(crossPortfolioCommand))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*same portfolio and have the same account type*");
    }

    [Fact]
    public async Task RetypingCannotLeaveAnExistingParentWithADifferentAccountType()
    {
        var parent = await AddDirectAccountAsync(
            code: "4001",
            name: "Income Parent",
            accountType: AccountType.Income);
        var child = await ExecuteCreateAsync(CreateCommand(
            "coa-parent-retype-child",
            code: "4108",
            name: "Income Child",
            accountType: AccountType.Income,
            parentAccountId: parent.Id));

        await FluentActions.Invoking(() => ExecuteUpdateAsync(UpdateCommand(
                "coa-parent-retype",
                child.Value.Account!.Id,
                accountType: AccountType.Expense)))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*same portfolio and have the same account type*");

        await FluentActions.Invoking(() => ExecuteUpdateAsync(UpdateCommand(
                "coa-parent-retype-parent",
                parent.Id,
                accountType: AccountType.Expense)))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*child accounts cannot be retyped*");

        var childId = child.Value.Account!.Id;
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts
                .Where(row => row.Id == parent.Id || row.Id == childId)
                .Select(row => row.AccountType)
                .ToListAsync())
            .Should().OnlyContain(type => type == AccountType.Income);
    }

    [Theory]
    [InlineData("operating-cash")]
    [InlineData("undeposited-funds")]
    [InlineData("security-deposit-trust-cash")]
    [InlineData("tenant-accounts-receivable")]
    [InlineData("mortgage-escrow-asset")]
    [InlineData("tenant-security-deposits-payable")]
    [InlineData("mortgage-payable")]
    [InlineData("owner-contributions")]
    [InlineData("owner-distributions")]
    [InlineData("retained-earnings")]
    public async Task ProtectedSystemControlAccounts_CannotBeDeactivated(string systemKey)
    {
        await SeedChartAsync();
        var account = await _context.Db.LedgerAccounts
            .SingleAsync(row => row.PortfolioId == PortfolioId && row.SystemKey == systemKey);
        _context.Db.ChangeTracker.Clear();

        var command = UpdateCommand(
            "coa-protected-deactivate",
            account.Id,
            isActive: false);
        await FluentActions.Invoking(() => ExecuteUpdateAsync(command))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*cannot be deactivated*");

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts
                .Where(row => row.Id == account.Id)
                .Select(row => row.IsActive)
                .SingleAsync())
            .Should().BeTrue();
    }

    [Fact]
    public async Task PostedAccounts_CannotBeRetypedOrDeleted()
    {
        await SeedChartAsync();
        var created = await ExecuteCreateAsync(CreateCommand(
            "coa-posted-account",
            code: "4104",
            name: "Posted Income"));
        var accountId = created.Value.Account!.Id;
        _context.Db.ChangeTracker.Clear();
        var cashId = await _context.Db.LedgerAccounts
            .Where(row => row.PortfolioId == PortfolioId && row.SystemKey == "operating-cash")
            .Select(row => row.Id)
            .SingleAsync();
        await PostAsync(cashId, accountId, "coa-posted-history");

        await FluentActions.Invoking(() => ExecuteUpdateAsync(UpdateCommand(
                "coa-posted-retype",
                accountId,
                accountType: AccountType.Expense)))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*posted journal lines cannot be retyped*");

        await FluentActions.Invoking(() => ExecuteUpdateAsync(UpdateCommand(
                "coa-posted-delete",
                accountId,
                delete: true)))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*posted journal lines cannot be deleted*");

        _context.Db.ChangeTracker.Clear();
        var unchanged = await _context.Db.LedgerAccounts.SingleAsync(row => row.Id == accountId);
        unchanged.AccountType.Should().Be(AccountType.Income);
        unchanged.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UnpostedUserAccount_CanBeRetypedWithDerivedBalance_Deactivated_AndDeleted()
    {
        var created = await ExecuteCreateAsync(CreateCommand(
            "coa-update-account",
            code: "4105",
            name: "Update Me"));
        var accountId = created.Value.Account!.Id;

        var updated = await ExecuteUpdateAsync(UpdateCommand(
            "coa-update-retype",
            accountId,
            name: "Updated Expense",
            accountType: AccountType.Expense,
            isActive: false));
        updated.Value.Account.Should().NotBeNull();
        updated.Value.Account!.AccountType.Should().Be(AccountType.Expense);
        updated.Value.Account.NormalBalance.Should().Be(NormalBalance.Debit);
        updated.Value.Account.Name.Should().Be("Updated Expense");
        updated.Value.Account.IsActive.Should().BeFalse();

        var replay = await ExecuteUpdateAsync(UpdateCommand(
            "coa-update-retype",
            accountId,
            name: "Updated Expense",
            accountType: AccountType.Expense,
            isActive: false));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(updated.Value);

        var unchanged = await ExecuteUpdateAsync(UpdateCommand(
            "coa-update-unchanged",
            accountId,
            name: "Updated Expense",
            accountType: AccountType.Expense,
            isActive: false));
        unchanged.Value.Outcome.Should().Be(LedgerAccountMutationOutcome.Applied);

        var deletedAccount = await ExecuteCreateAsync(CreateCommand(
            "coa-delete-account",
            code: "4106",
            name: "Delete Me"));
        var deleted = await ExecuteUpdateAsync(UpdateCommand(
            "coa-delete-account",
            deletedAccount.Value.Account!.Id,
            delete: true));
        deleted.Value.Outcome.Should().Be(LedgerAccountMutationOutcome.Deleted);
        deleted.Value.Account!.Id.Should().Be(deletedAccount.Value.Account.Id);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts.AnyAsync(row => row.Id == deletedAccount.Value.Account.Id))
            .Should().BeFalse();
        (await _context.Db.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == "ledger-account-update:coa-update-retype"))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.CountAsync(row =>
                row.EntityType == nameof(LedgerAccount)
                && row.EntityId == accountId
                && row.Operation == AuditLogOperation.Updated))
            .Should().Be(1, "an unchanged update must not select a database audit clock");
        (await _context.Db.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == "ledger-account-update:coa-update-unchanged"))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.CountAsync(row =>
                row.EntityType == nameof(LedgerAccount)
                && row.EntityId == deletedAccount.Value.Account.Id
                && row.Operation == AuditLogOperation.Deleted))
            .Should().Be(1);
    }

    [Fact]
    public async Task ExplicitCodeDuplicate_IsRejectedWithoutASecondAccount()
    {
        await ExecuteCreateAsync(CreateCommand(
            "coa-explicit-code-first",
            code: "4107",
            name: "First Explicit Code"));

        await FluentActions.Invoking(() => ExecuteCreateAsync(CreateCommand(
                "coa-explicit-code-duplicate",
                code: "4107",
                name: "Duplicate Explicit Code")))
            .Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*code already exists*");

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LedgerAccounts.CountAsync(row =>
                row.PortfolioId == PortfolioId && row.Code == "4107"))
            .Should().Be(1);
    }

    private async Task<AtomicCommandOutcome<LedgerAccountMutationResult>> ExecuteCreateAsync(
        CreateLedgerAccountCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = new CreateLedgerAccountRule(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        return await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(command.DeliveryIdempotencyKey,
                AccountingWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private async Task<AtomicCommandOutcome<LedgerAccountMutationResult>> ExecuteUpdateAsync(
        UpdateLedgerAccountCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = new UpdateLedgerAccountRule(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        return await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(command.DeliveryIdempotencyKey,
                AccountingWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private CreateLedgerAccountCommand CreateCommand(
        string key,
        string code = "",
        string name = "Test Income",
        AccountType accountType = AccountType.Income,
        int? parentAccountId = null,
        string? systemKey = null,
        bool isActive = true,
        WorkspaceReadScope? scope = null) =>
        CreateCommand(
            scope ?? _scope,
            key,
            code,
            name,
            accountType,
            parentAccountId,
            systemKey,
            isActive);

    private static CreateLedgerAccountCommand CreateCommand(
        WorkspaceReadScope scope,
        string key,
        string code,
        string name,
        AccountType accountType,
        int? parentAccountId,
        string? systemKey,
        bool isActive) => new(
        scope.PortfolioId,
        scope.UserId,
        scope.SessionId,
        scope.AccessContextId,
        scope.AccessRevision,
        code,
        name,
        accountType,
        parentAccountId,
        systemKey,
        ScheduleECategory.Other,
        isActive,
        key);

    private UpdateLedgerAccountCommand UpdateCommand(
        string key,
        int accountId,
        string? name = null,
        AccountType? accountType = null,
        int? parentAccountId = null,
        bool parentAccountIdSpecified = false,
        bool? isActive = null,
        bool delete = false,
        WorkspaceReadScope? scope = null) =>
        new(
            (scope ?? _scope).PortfolioId,
            (scope ?? _scope).UserId,
            (scope ?? _scope).SessionId,
            (scope ?? _scope).AccessContextId,
            (scope ?? _scope).AccessRevision,
            accountId,
            name,
            accountType,
            parentAccountId,
            parentAccountIdSpecified,
            ScheduleECategory.Other,
            isActive,
            delete,
            key);

    private async Task SeedChartAsync()
    {
        await new ChartOfAccountsSeedService(_context.Db).SeedAsync(PortfolioId);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task<LedgerAccount> AddDirectAccountAsync(
        string code,
        string name,
        AccountType accountType,
        int portfolioId = PortfolioId,
        bool isSystem = false,
        string? systemKey = null)
    {
        var now = DateTime.UtcNow;
        var account = new LedgerAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            Code = code,
            Name = name,
            AccountType = accountType,
            NormalBalance = accountType is AccountType.Asset or AccountType.Expense
                ? NormalBalance.Debit
                : NormalBalance.Credit,
            SystemKey = systemKey,
            IsSystem = isSystem,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _context.Db.LedgerAccounts.Add(account);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return account;
    }

    private async Task<Property> AddPropertyAsync()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Capability Test Property",
            AddressLine1 = "1 Test Way",
            City = "Testville",
            State = "NY",
            PostalCode = "10001",
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return property;
    }

    private async Task PostAsync(int debitAccountId, int creditAccountId, string businessKey)
    {
        await new AccountingPostingService(_context.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = PortfolioId,
            SourceType = JournalSourceType.TenantCharge,
            SourceId = Math.Abs(businessKey.GetHashCode()),
            SourceBusinessKey = businessKey,
            PostingRuleVersion = 1,
            EffectiveOn = new DateOnly(2026, 8, 1),
            Currency = "USD",
            Description = "Ledger account atomic test posting",
            AttemptId = Guid.NewGuid(),
            UserId = _scope.UserId,
            AtomicReceiptId = Guid.NewGuid(),
            Lines =
            [
                new AccountingProposedLine
                {
                    LedgerAccountId = debitAccountId,
                    DebitAmount = 100m,
                },
                new AccountingProposedLine
                {
                    LedgerAccountId = creditAccountId,
                    CreditAmount = 100m,
                },
            ],
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private static ServiceProvider CreateServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(InterceptorNow));
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
