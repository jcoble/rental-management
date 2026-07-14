using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Import;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
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
        _sut = new CsvImportService(
            _atomic,
            _atomic,
            _atomic,
            _atomic);
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
    // Unit: parses rows and maps database-owned resolution/persistence receipts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnitCommit_SerializesPropertyName_AndMapsAtomicReceipt()
    {
        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "maple court,101,2,1.5,\"$1,200\"\n";   // name differs only in case; quoted currency rent

        var result = await _sut.ImportAsync(
            _scope, "Unit", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        result.TotalRows.Should().Be(1);
        result.ValidRows.Should().Be(1);
        result.CreatedRows.Should().Be(1);
        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().Be(50_000);

        _atomic.UnitCommands.Should().ContainSingle();
        var row = _atomic.UnitCommands.Single().Rows.Should().ContainSingle().Subject;
        row.PropertyId.Should().BeNull();
        row.PropertyName.Should().Be("maple court");
        row.UnitNumber.Should().Be("101");
        row.Bedrooms.Should().Be(2m);
        row.Bathrooms.Should().Be(1.5m);
        row.MarketRent.Should().Be(1200m);
        (await _ctx.Db.Units.CountAsync()).Should().Be(0,
            "this service-level test maps the atomic receipt; PostgreSQL owns persistence");
    }

    [Fact]
    public async Task UnitPreview_MapsUnknownPropertyResolutionError()
    {
        _atomic.ReturnUnitPreviewError("Property was not found in this portfolio.");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Nonexistent Place,101,2,1,1000\n";

        var result = await _sut.ImportAsync(_scope, "Unit", Csv(csv), dryRun: true);

        result.ValidRows.Should().Be(0);
        result.CreatedRows.Should().Be(0);
        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*not found*");
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnitPreview_MapsAmbiguousPropertyResolutionError()
    {
        _atomic.ReturnUnitPreviewError(
            "Property name matches more than one property; use propertyId.");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Maple Court,101,2,1,1000\n";

        var result = await _sut.ImportAsync(_scope, "Unit", Csv(csv), dryRun: true);

        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*more than one property*");
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
        _atomic.PaymentCommands.Should().ContainSingle();
        var command = _atomic.PaymentCommands.Single();
        using var paymentRows = JsonDocument.Parse(command.RowsJson);
        var imported = paymentRows.RootElement[0];
        imported.GetProperty("RelationshipNumber").GetString().Should().Be("REL-100");
        imported.GetProperty("Amount").GetDecimal().Should().Be(1200m);
        imported.GetProperty("PaidOn").GetString().Should().Be("2025-01-05");
        imported.GetProperty("ExternalReference").GetString().Should().Be("bank-1");
        imported.GetProperty("DeliveryKey").GetString().Should().Contain("operation-digest:2");
        (await _ctx.Db.TenantLedgerEntries.CountAsync()).Should().Be(0,
            "the fake atomic unit returns the canonical receipt id without persisting a ledger row");
    }

    [Fact]
    public async Task ExpenseImport_MapsAtomicDuplicateReceiptAcrossReimport()
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

        _atomic.CoreCommands.Should().ContainSingle();
        _atomic.CoreCommands.Single().Domain.Should().Be(AtomicCoreCsvImportDomain.Expense);

        var second = await _sut.ImportAsync(
            _scope, "mortgage payments", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        _atomic.CoreCommands.Should().HaveCount(2);
    }

    [Fact]
    public async Task LoanImport_MapsAtomicDuplicateReceiptAcrossReimport()
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

        _atomic.CoreCommands.Should().ContainSingle();
        _atomic.CoreCommands.Single().Domain.Should().Be(AtomicCoreCsvImportDomain.Loan);

        var second = await _sut.ImportAsync(
            _scope, "loan", Csv(csv), dryRun: false, commandContext: LiveCommandContext);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        _atomic.CoreCommands.Should().HaveCount(2);
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
        : IAtomicUnitOfWork, IUnitCsvImportPreviewQuery, ICoreCsvImportPreviewQuery,
          IPaymentCsvImportPreviewQuery
    {
        public List<AtomicPaymentCsvImportCommand> PaymentCommands { get; } = [];
        public List<AtomicCoreCsvImportCommand> CoreCommands { get; } = [];
        public List<AtomicUnitCsvImportCommand> UnitCommands { get; } = [];
        private readonly HashSet<string> _coreKeys = new(StringComparer.Ordinal);
        private AtomicUnitImportBatchResult? _nextUnitPreview;

        public void ReturnUnitPreviewError(string error)
        {
            var row = new AtomicUnitImportRowResult(
                2, false, false, null, null, "101", 2m, 1m, 1000m, [error]);
            _nextUnitPreview = new AtomicUnitImportBatchResult(
                true, [row], [], 1, 0, 0, 0);
        }

        public Task<AtomicUnitImportBatchResult> PreviewAsync(
            WorkspaceReadScope scope,
            IReadOnlyList<AtomicUnitImportRow> rows,
            CancellationToken ct = default)
        {
            var result = _nextUnitPreview ?? UnitResult(rows, created: false);
            _nextUnitPreview = null;
            return Task.FromResult(result);
        }

        public Task<AtomicCoreCsvImportBatchResult> PreviewAsync(
            WorkspaceReadScope scope,
            AtomicCoreCsvImportDomain domain,
            string rowsJson,
            CancellationToken ct = default) =>
            Task.FromResult(CoreResult(domain, rowsJson, created: false));

        public Task<AtomicPaymentCsvImportBatchResult> PreviewAsync(
            WorkspaceReadScope scope,
            string rowsJson,
            CancellationToken ct = default) =>
            Task.FromResult(PaymentResult(rowsJson, created: false));

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
                UnitCommands.Add(unit);
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
                var batch = CoreResult(core.Domain, core.RowsJson, created: true);
                var result = new AtomicCoreCsvImportResult(
                    batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
                    batch.CreatedCount, batch.DuplicateRows);
                return Task.FromResult((AtomicCommandOutcome<TResult>)(object)
                    new AtomicCommandOutcome<AtomicCoreCsvImportResult>(
                        result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
            }
            if (command is AtomicPaymentCsvImportCommand payment
                && typeof(TResult) == typeof(AtomicPaymentCsvImportResult))
            {
                PaymentCommands.Add(payment);
                var batch = PaymentResult(payment.RowsJson, created: true);
                var result = new AtomicPaymentCsvImportResult(
                    batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
                    batch.CreatedCount, batch.DuplicateRows);
                return Task.FromResult((AtomicCommandOutcome<TResult>)(object)
                    new AtomicCommandOutcome<AtomicPaymentCsvImportResult>(
                        result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
            }
            throw new InvalidOperationException("Unexpected atomic command in CSV import test.");
        }

        private static AtomicPaymentCsvImportBatchResult PaymentResult(
            string rowsJson,
            bool created)
        {
            using var document = JsonDocument.Parse(rowsJson);
            var results = document.RootElement.EnumerateArray().Select((row, index) =>
            {
                var errors = row.GetProperty("Errors").EnumerateArray()
                    .Select(error => error.GetString() ?? string.Empty).ToArray();
                return new AtomicPaymentCsvImportRowResult(
                    row.GetProperty("RowNumber").GetInt32(),
                    errors.Length == 0,
                    false,
                    created && errors.Length == 0 ? 9_000_000_001L + index : null,
                    errors.Length == 0 ? 700 : null,
                    errors);
            }).ToArray();
            var createdRows = results.Where(row => row.CreatedId.HasValue).ToArray();
            return new AtomicPaymentCsvImportBatchResult(
                true, results, createdRows, results.Length,
                results.Count(row => row.Valid), createdRows.Length, 0);
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

        private AtomicCoreCsvImportBatchResult CoreResult(
            AtomicCoreCsvImportDomain domain,
            string rowsJson,
            bool created)
        {
            using var document = JsonDocument.Parse(rowsJson);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = document.RootElement.EnumerateArray().Select((row, index) =>
            {
                var errors = row.GetProperty("Errors").EnumerateArray()
                    .Select(error => error.GetString() ?? string.Empty).ToArray();
                var key = domain switch
                {
                    AtomicCoreCsvImportDomain.Expense => string.Join("|",
                        row.GetProperty("PropertyName").GetString(),
                        row.GetProperty("Amount").GetDecimal(),
                        row.GetProperty("IncurredAt").GetDateTime(),
                        row.GetProperty("Description").GetString()),
                    AtomicCoreCsvImportDomain.Loan => string.Join("|",
                        row.GetProperty("PropertyName").GetString(),
                        row.GetProperty("Lender").GetString(),
                        row.GetProperty("OriginalAmount").GetDecimal(),
                        row.GetProperty("StartDate").GetDateTime()),
                    _ => $"{domain}:{index}",
                };
                var duplicate = errors.Length == 0
                    && (!seen.Add(key) || _coreKeys.Contains(key));
                if (created && errors.Length == 0 && !duplicate) _coreKeys.Add(key);
                return new AtomicCoreCsvImportRowResult(
                    row.GetProperty("RowNumber").GetInt32(),
                    errors.Length == 0,
                    duplicate,
                    created && errors.Length == 0 && !duplicate ? 60_000 + index : null,
                    null,
                    errors);
            }).ToArray();
            var createdRows = results.Where(row => row.CreatedId.HasValue).ToArray();
            return new AtomicCoreCsvImportBatchResult(
                true, results, createdRows, results.Length,
                results.Count(row => row.Valid), createdRows.Length,
                results.Count(row => row.IsDuplicate));
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
