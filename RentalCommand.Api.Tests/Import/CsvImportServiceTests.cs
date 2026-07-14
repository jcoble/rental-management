using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Import;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Import;

public class CsvImportServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx;
    private readonly CsvImportService _sut;
    private readonly CapturingAtomicUnitOfWork _atomic = new();
    private readonly WorkspaceReadScope _scope;
    private static readonly CsvImportCommandContext LiveCommandContext = new(
        ActorUserId: 41,
        AuthSessionId: Guid.Parse("e5b8ab13-7e95-47af-bcda-a0df231bfb84"),
        AccessContextId: 42,
        AccessRevision: 9,
        OperationKeyDigest: "operation-digest");

    public CsvImportServiceTests()
    {
        _ctx = new SqliteTestContext();
        _ctx.Db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();
        _ctx.Db.Users.Add(new ApplicationUser
        {
            Id = 1,
            UserName = "csv-import-tests@example.test",
            NormalizedUserName = "CSV-IMPORT-TESTS@EXAMPLE.TEST",
            Email = "csv-import-tests@example.test",
            NormalizedEmail = "CSV-IMPORT-TESTS@EXAMPLE.TEST",
            DisplayName = "CSV Import Test Actor",
            CreatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(CsvImportServiceTests));
        var noop = new NoopDataUpdateService();
        _sut = new CsvImportService(
            _ctx.Db,
            _atomic,
            _atomic,
            _atomic,
            new ExpenseService(_ctx.Db, noop, Mock.Of<IFileStorage>(), TimeProvider.System),
            new LoanService(_ctx.Db, noop, TimeProvider.System));
    }

    public void Dispose() => _ctx.Dispose();

    private static Stream Csv(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    // -------------------------------------------------------------------------
    // Tenant dry-run: reports valid/invalid rows and creates nothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TenantDryRun_ReportsValidAndInvalidRows_CreatesNothing()
    {
        const string csv =
            "firstName,lastName,email,phone\n" +
            "Frank,Coble,frank@example.com,555-1212\n" +   // valid
            ",NoFirst,bad@example.com,555-0000\n" +         // invalid: missing firstName
            "Jane,Doe,not-an-email,\n";                     // invalid: bad email

        var result = await _sut.ImportAsync(_scope, "tenant", Csv(csv), dryRun: true);

        result.EntityType.Should().Be("Tenant");
        result.DryRun.Should().BeTrue();
        result.TotalRows.Should().Be(3);
        result.ValidRows.Should().Be(1);
        result.CreatedRows.Should().Be(0);

        result.Rows.Should().HaveCount(3);
        result.Rows[0].RowNumber.Should().Be(2); // header is row 1
        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().BeNull();
        result.Rows[1].Valid.Should().BeFalse();
        result.Rows[1].Errors.Should().NotBeEmpty();
        result.Rows[2].Valid.Should().BeFalse();
        result.Rows[2].Errors.Should().NotBeEmpty();

        (await _ctx.Db.Tenants.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Tenant commit: creates the valid rows, skips invalid ones
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TenantCommit_CreatesValidRows_SkipsInvalidRows()
    {
        const string csv =
            "firstName,lastName,email,phone\n" +
            "Frank,Coble,frank@example.com,555-1212\n" +   // valid
            ",NoFirst,,\n" +                                 // invalid: missing firstName
            "Jane,Doe,,555-9999\n";                          // valid

        var result = await _sut.ImportAsync(
            _scope, "Tenant", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        result.TotalRows.Should().Be(3);
        result.ValidRows.Should().Be(2);
        result.CreatedRows.Should().Be(2);

        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().NotBeNull();
        result.Rows[1].Valid.Should().BeFalse();
        result.Rows[1].CreatedId.Should().BeNull();
        result.Rows[2].Valid.Should().BeTrue();
        result.Rows[2].CreatedId.Should().NotBeNull();

        _atomic.CoreCommands.Should().ContainSingle();
        _atomic.CoreCommands.Single().Domain.Should().Be(AtomicCoreCsvImportDomain.Tenant);
    }

    // -------------------------------------------------------------------------
    // Unit: resolves the owning property by name (case-insensitive, in-portfolio)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnitImport_ResolvesPropertyByName_AndCreatesUnit()
    {
        var property = SeedProperty("Maple Court");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "maple court,101,2,1.5,\"$1,200\"\n";   // name differs only in case; quoted currency rent

        var result = await _sut.ImportAsync(
            _scope, "Unit", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        result.TotalRows.Should().Be(1);
        result.ValidRows.Should().Be(1);
        result.CreatedRows.Should().Be(1);
        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().NotBeNull();

        var unit = await _ctx.Db.Units.SingleAsync();
        unit.PropertyId.Should().Be(property.Id);
        unit.UnitNumber.Should().Be("101");
        unit.Bedrooms.Should().Be(2m);
        unit.Bathrooms.Should().Be(1.5m);
        unit.MarketRent.Should().Be(1200m);
    }

    [Fact]
    public async Task UnitImport_UnknownPropertyName_IsAClearPerRowError()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Nonexistent Place,101,2,1,1000\n";

        var result = await _sut.ImportAsync(
            _scope, "Unit", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        result.ValidRows.Should().Be(0);
        result.CreatedRows.Should().Be(0);
        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*No property named*");
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnitImport_AmbiguousPropertyName_IsAClearPerRowError()
    {
        SeedProperty("Maple Court");
        SeedProperty("Maple Court"); // duplicate name → ambiguous

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Maple Court,101,2,1,1000\n";

        var result = await _sut.ImportAsync(_scope, "Unit", Csv(csv), dryRun: true);

        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*ambiguous*");
    }

    // -------------------------------------------------------------------------
    // Property: type column defaults sensibly and parses case-insensitively
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PropertyImport_DefaultsTypeWhenBlank_AndParsesTypeName()
    {
        const string csv =
            "name,addressLine1,city,state,postalCode,type\n" +
            "House A,1 Main St,Springfield,IL,62701,singlefamily\n" +   // explicit type, lowercase
            "House B,2 Main St,Springfield,IL,62701,\n";                 // blank type → default

        var result = await _sut.ImportAsync(
            _scope, "Property", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        result.CreatedRows.Should().Be(2);
        _atomic.CoreCommands.Should().ContainSingle();
        var command = _atomic.CoreCommands.Single();
        command.Domain.Should().Be(AtomicCoreCsvImportDomain.Property);
        using var rows = JsonDocument.Parse(command.RowsJson);
        rows.RootElement[0].GetProperty("PropertyType").GetInt32()
            .Should().Be((int)PropertyType.SingleFamily);
        rows.RootElement[1].GetProperty("PropertyType").GetInt32()
            .Should().Be((int)PropertyType.MultiFamily); // CreatePropertyRequest default
    }

    // -------------------------------------------------------------------------
    // Financial imports: create through domain services and skip natural-key duplicates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PaymentImport_ResolvesRelationshipToTenantAccount_AndPostsCanonicalReceipt()
    {
        SeedTenantAccount("REL-100", tenantAccountId: 700);
        const string csv =
            "relationshipNumber,propertyName,unitNumber,paymentType,amount,paidDate,method,externalReference,notes\n" +
            "REL-100,,,Rent,1200,2025-01-05,ACH,bank-1,January rent\n";
        var context = new CsvImportCommandContext(
            ActorUserId: 41,
            AuthSessionId: Guid.Parse("e5b8ab13-7e95-47af-bcda-a0df231bfb84"),
            AccessContextId: 42,
            AccessRevision: 9,
            OperationKeyDigest: "operation-digest");

        var result = await _sut.ImportAsync(
            _scope, "payments", Csv(csv), dryRun: false, commandContext: context);

        result.CreatedRows.Should().Be(1);
        result.Rows.Should().ContainSingle().Which.CreatedId.Should().Be(9_000_000_001L);
        _atomic.Commands.Should().ContainSingle();
        var command = _atomic.Commands.Single();
        command.TenantAccountId.Should().Be(700);
        command.Amount.Should().Be(1200m);
        command.EffectiveOn.Should().Be(new DateOnly(2025, 1, 5));
        command.ExternalReference.Should().Be("bank-1");
        command.RequiredCapability.Should().Be("money.payments.manage");
        command.BusinessKey.Should().StartWith("csv-receipt:");
        command.DeliveryIdempotencyKey.Should().Contain("operation-digest:2");
        (await _ctx.Db.TenantLedgerEntries.CountAsync()).Should().Be(0,
            "the fake atomic unit returns the canonical receipt id without persisting a ledger row");
    }

    [Fact]
    public async Task ExpenseImport_CreatesMortgageInterestExpense_AndSkipsDuplicateReimport()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,category,description,amount,incurredAt,paidAt,notes\n" +
            "Maple Court,MortgageInterest,January mortgage interest,800,2025-01-15,2025-01-15,Imported history\n" +
            "Maple Court,MortgageInterest,January mortgage interest,800,2025-01-15,2025-01-15,Duplicate row\n";

        var first = await _sut.ImportAsync(
            _scope, "expenses", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        first.EntityType.Should().Be("Expense");
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(1);
        first.Rows[1].IsDuplicate.Should().BeTrue();

        var stored = await _ctx.Db.Expenses.SingleAsync();
        stored.Category.Should().Be(ScheduleECategory.MortgageInterest);
        stored.Status.Should().Be(ExpenseStatus.Paid);
        stored.Amount.Should().Be(800m);

        var second = await _sut.ImportAsync(
            _scope, "mortgage payments", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        (await _ctx.Db.Expenses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LoanImport_CreatesLoan_AndSkipsDuplicateReimport()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,lender,originalAmount,currentBalance,annualInterestRatePct,termMonths,startDate,dayOfMonthDue,monthlyPrincipalInterest,monthlyEscrow\n" +
            "Maple Court,Acme Bank,200000,198500,6.25,360,2024-01-01,1,1231.43,350\n" +
            "Maple Court,Acme Bank,200000,198500,6.25,360,2024-01-01,1,1231.43,350\n";

        var first = await _sut.ImportAsync(
            _scope, "mortgages", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        first.EntityType.Should().Be("Loan");
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(1);
        first.Rows[1].IsDuplicate.Should().BeTrue();

        var stored = await _ctx.Db.Loans.SingleAsync();
        stored.Lender.Should().Be("Acme Bank");
        stored.OriginalAmount.Should().Be(200000m);
        stored.CurrentBalance.Should().Be(198500m);

        var second = await _sut.ImportAsync(
            _scope, "loan", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        (await _ctx.Db.Loans.CountAsync()).Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Templates + unsupported entity types
    // -------------------------------------------------------------------------

    [Fact]
    public void Template_ReturnsHeaderRow_PerEntityType()
    {
        _sut.GetTemplate("tenant").Should().Be("firstName,lastName,email,phone");
        _sut.GetTemplate("Property").Should().StartWith("name,addressLine1");
        _sut.GetTemplate("UNIT").Should().Contain("unitNumber");
        _sut.GetTemplate("payments").Should().Contain("relationshipNumber");
        _sut.GetTemplate("expenses").Should().Contain("incurredAt");
        _sut.GetTemplate("mortgages").Should().Contain("originalAmount");
    }

    [Fact]
    public async Task UnsupportedEntityType_Throws()
    {
        var act = async () => await _sut.ImportAsync(_scope, "vendor", Csv("x\n"), dryRun: true);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private Property SeedProperty(string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            PropertyType = PropertyType.MultiFamily,
            Status = PropertyStatus.Active,
            AddressLine1 = "1 Test St",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private void SeedTenantAccount(string relationshipNumber, int tenantAccountId)
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty("Maple Court");
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "101",
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = relationshipNumber,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(management);
        _ctx.Db.SaveChanges();
        _ctx.Db.TenantAccounts.Add(new TenantAccount
        {
            Id = tenantAccountId,
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{tenantAccountId}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        });
        _ctx.Db.SaveChanges();
    }

    private sealed class CapturingAtomicUnitOfWork
        : IAtomicUnitOfWork, IUnitCsvImportPreviewQuery, ICoreCsvImportPreviewQuery
    {
        public List<RecordTenantReceiptCommand> Commands { get; } = [];
        public List<AtomicCoreCsvImportCommand> CoreCommands { get; } = [];

        public Task<AtomicUnitImportBatchResult> PreviewAsync(
            WorkspaceReadScope scope,
            IReadOnlyList<AtomicUnitImportRow> rows,
            CancellationToken ct = default) => Task.FromResult(UnitResult(rows, created: false));

        public Task<AtomicCoreCsvImportBatchResult> PreviewAsync(
            WorkspaceReadScope scope,
            AtomicCoreCsvImportDomain domain,
            string rowsJson,
            CancellationToken ct = default) =>
            Task.FromResult(CoreResult(rowsJson, created: false));

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            IAtomicResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            if (command is AtomicUnitCsvImportCommand unit
                && typeof(TResult) == typeof(AtomicUnitCsvImportResult))
            {
                var batch = UnitResult(unit.Rows, created: true);
                var result = new AtomicUnitCsvImportResult(
                    batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
                    batch.CreatedCount, batch.DuplicateRows);
                return Task.FromResult((AtomicCommandOutcome<TResult>)(object)
                    new AtomicCommandOutcome<AtomicUnitCsvImportResult>(
                        result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
            }
            if (command is AtomicCoreCsvImportCommand core
                && typeof(TResult) == typeof(AtomicCoreCsvImportResult))
            {
                CoreCommands.Add(core);
                var batch = CoreResult(core.RowsJson, created: true);
                var result = new AtomicCoreCsvImportResult(
                    batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
                    batch.CreatedCount, batch.DuplicateRows);
                return Task.FromResult((AtomicCommandOutcome<TResult>)(object)
                    new AtomicCommandOutcome<AtomicCoreCsvImportResult>(
                        result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
            }
            if (command is not RecordTenantReceiptCommand receipt
                || typeof(TResult) != typeof(RecordTenantReceiptResult))
                throw new InvalidOperationException("Unexpected atomic command in CSV import test.");
            Commands.Add(receipt);
            var result = new RecordTenantReceiptResult(
                true, receipt.TenantAccountId, 9_000_000_001L, 9_000_000_002L,
                receipt.Amount, receipt.Amount, 1);
            return Task.FromResult((AtomicCommandOutcome<TResult>)(object)
                new AtomicCommandOutcome<RecordTenantReceiptResult>(
                    result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }

        private static AtomicUnitImportBatchResult UnitResult(
            IReadOnlyList<AtomicUnitImportRow> rows,
            bool created)
        {
            var results = rows.Select((row, index) => new AtomicUnitImportRowResult(
                row.RowNumber,
                row.Errors.Length == 0,
                false,
                created && row.Errors.Length == 0 ? 50_000 + index : null,
                row.PropertyId,
                row.UnitNumber,
                row.Bedrooms,
                row.Bathrooms,
                row.MarketRent,
                row.Errors)).ToArray();
            var createdRows = results.Where(row => row.CreatedId.HasValue).ToArray();
            return new AtomicUnitImportBatchResult(
                true, results, createdRows, results.Length,
                results.Count(row => row.Valid), createdRows.Length,
                results.Count(row => row.IsDuplicate));
        }

        private static AtomicCoreCsvImportBatchResult CoreResult(string rowsJson, bool created)
        {
            using var document = JsonDocument.Parse(rowsJson);
            var results = document.RootElement.EnumerateArray().Select((row, index) =>
            {
                var errors = row.GetProperty("Errors").EnumerateArray()
                    .Select(error => error.GetString() ?? string.Empty).ToArray();
                return new AtomicCoreCsvImportRowResult(
                    row.GetProperty("RowNumber").GetInt32(),
                    errors.Length == 0,
                    false,
                    created && errors.Length == 0 ? 60_000 + index : null,
                    null,
                    errors);
            }).ToArray();
            var createdRows = results.Where(row => row.CreatedId.HasValue).ToArray();
            return new AtomicCoreCsvImportBatchResult(
                true, results, createdRows, results.Length,
                results.Count(row => row.Valid), createdRows.Length, 0);
        }
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
