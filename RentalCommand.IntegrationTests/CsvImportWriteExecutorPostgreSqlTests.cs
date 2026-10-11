using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Import;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Import;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Import;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection3.Name)]
public sealed class CsvImportWriteExecutorPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime SeparatedAuditClock =
        new(2040, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public CsvImportWriteExecutorPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public async Task CoreImport_PreservesAliasPartialBatchFingerprintRetryClockOutboxAndStaleAuthorization()
    {
        var scope = await SeedScopeAsync(DateTime.UtcNow, "core");
        var property = await _context.Db.Properties.AsNoTracking().SingleAsync(row =>
            row.Name == "CSV core alias property");
        const string operationKey = "family-p4-core-csv";
        const string csv =
            "propertyName,lender,originalAmount,currentBalance,annualInterestRatePct,termMonths,startDate,dayOfMonthDue,monthlyPrincipalInterest,monthlyEscrow\n" +
            "csv CORE alias PROPERTY,Alias Bank,200000,198500,6.25,360,2024-01-01,1,1231.43,350\n" +
            "missing property,Missing Bank,100000,99000,5,120,2024-02-01,1,1000,200\n";

        CsvImportResult first;
        CsvImportResult replay;
        var databaseBefore = DateTime.UtcNow;
        await using (var services = BuildServices())
        await using (var requestScope = services.CreateAsyncScope())
        {
            var sut = requestScope.ServiceProvider.GetRequiredService<CsvImportService>();
            first = await sut.ImportAsync(scope, "loan", Csv(csv), false,
                CommandContext(scope, operationKey));
            replay = await sut.ImportAsync(scope, "loan", Csv(csv), false,
                CommandContext(scope, operationKey));
        }
        var databaseAfter = DateTime.UtcNow;

        replay.Should().BeEquivalentTo(first);
        first.TotalRows.Should().Be(2);
        first.CreatedRows.Should().Be(1);
        first.ValidRows.Should().Be(1);
        first.DuplicateRows.Should().Be(0);
        var loan = await _context.Db.Loans.AsNoTracking().SingleAsync(row =>
            row.Lender == "Alias Bank");
        loan.PropertyId.Should().Be(property.Id);
        first.Rows.Should().BeEquivalentTo(
            [
                new CsvImportRowResult
                {
                    RowNumber = 2,
                    Valid = true,
                    Errors = [],
                    CreatedId = loan.Id,
                    IsDuplicate = false,
                    SkipReason = null,
                },
                new CsvImportRowResult
                {
                    RowNumber = 3,
                    Valid = false,
                    Errors = ["Property was not found in this portfolio."],
                    CreatedId = null,
                    IsDuplicate = false,
                    SkipReason = null,
                },
            ], options => options.WithStrictOrdering());

        var identity = new AtomicCommandIdentity(
            "loan.csv-import", $"{scope.PortfolioId}:{scope.AccessContextId}:{operationKey}");
        var receipt = await ReceiptAsync(identity);
        var rowsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RowNumber = 2, PropertyName = "csv CORE alias PROPERTY",
                Lender = "Alias Bank", OriginalAmount = 200000m,
                CurrentBalance = (decimal?)198500m, AnnualInterestRatePct = 6.25m,
                TermMonths = 360,
                StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DayOfMonthDue = 1, MonthlyPrincipalInterest = 1231.43m,
                MonthlyEscrow = 350m,
                Errors = Array.Empty<string>(),
            },
            new
            {
                RowNumber = 3, PropertyName = "missing property",
                Lender = "Missing Bank", OriginalAmount = 100000m,
                CurrentBalance = (decimal?)99000m, AnnualInterestRatePct = 5m,
                TermMonths = 120,
                StartDate = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                DayOfMonthDue = 1, MonthlyPrincipalInterest = 1000m,
                MonthlyEscrow = 200m,
                Errors = Array.Empty<string>(),
            },
        });
        var frozenCommand = new AtomicCoreCsvImportCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, AtomicCoreCsvImportDomain.Loan, operationKey, rowsJson);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenCommand));
        receipt.ResultContract.Should().Be(AtomicCoreCsvImport.Codec.ContractName);
        AtomicCoreCsvImport.Codec.Deserialize(receipt.ResultJson!).Rows.Should().BeEquivalentTo(
            [
                new AtomicCoreCsvImportRowResult(
                    2, true, false, loan.Id, property.Id, []),
                new AtomicCoreCsvImportRowResult(
                    3, false, false, null, null,
                    ["Property was not found in this portfolio."]),
            ], options => options.WithStrictOrdering());

        var audit = await AuditAsync(identity, nameof(Loan));
        audit.Timestamp.Should().BeOnOrAfter(databaseBefore.AddSeconds(-1));
        audit.Timestamp.Should().BeOnOrBefore(databaseAfter.AddSeconds(1));
        audit.Timestamp.Should().NotBe(SeparatedAuditClock);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"loan-import:{operationKey}:loan:")))
            .Should().Be(1);

        await RevokeAsync(scope, DateTime.UtcNow);
        await using var staleServices = BuildServices();
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<CsvImportService>()
            .ImportAsync(scope, "loan", Csv(csv), false,
                CommandContext(scope, operationKey));
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Workspace access changed. Refresh and try again.");
    }

    [Fact]
    public async Task UnitImport_PreservesAliasPartialBatchFingerprintRetryClockAndOutbox()
    {
        var scope = await SeedScopeAsync(DateTime.UtcNow, "unit");
        var property = await _context.Db.Properties.AsNoTracking().SingleAsync(row =>
            row.Name == "CSV unit alias property");
        const string operationKey = "family-p4-unit-csv";
        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "csv UNIT alias PROPERTY,101,2,1.5,1200\n" +
            "missing property,102,1,1,900\n";

        CsvImportResult first;
        CsvImportResult replay;
        var databaseBefore = DateTime.UtcNow;
        await using (var services = BuildServices())
        await using (var requestScope = services.CreateAsyncScope())
        {
            var sut = requestScope.ServiceProvider.GetRequiredService<CsvImportService>();
            first = await sut.ImportAsync(scope, "unit", Csv(csv), false,
                CommandContext(scope, operationKey));
            replay = await sut.ImportAsync(scope, "unit", Csv(csv), false,
                CommandContext(scope, operationKey));

        }
        var databaseAfter = DateTime.UtcNow;

        replay.Should().BeEquivalentTo(first);
        first.TotalRows.Should().Be(2);
        first.ValidRows.Should().Be(1);
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(0);
        var unit = await _context.Db.Units.AsNoTracking().SingleAsync(row =>
            row.PropertyId == property.Id && row.UnitNumber == "101");
        first.Rows.Should().BeEquivalentTo(
            [
                new CsvImportRowResult
                {
                    RowNumber = 2,
                    Valid = true,
                    Errors = [],
                    CreatedId = unit.Id,
                    IsDuplicate = false,
                    SkipReason = null,
                },
                new CsvImportRowResult
                {
                    RowNumber = 3,
                    Valid = false,
                    Errors = ["Property was not found in this portfolio."],
                    CreatedId = null,
                    IsDuplicate = false,
                    SkipReason = null,
                },
            ], options => options.WithStrictOrdering());

        var identity = new AtomicCommandIdentity(
            "unit.csv-import", $"{scope.PortfolioId}:{scope.AccessContextId}:{operationKey}");
        var receipt = await ReceiptAsync(identity);
        var frozenCommand = new AtomicUnitCsvImportCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, operationKey,
            [
                new AtomicUnitImportRow(
                    2, null, "csv UNIT alias PROPERTY", "101", 2, 1.5m, 1200, []),
                new AtomicUnitImportRow(
                    3, null, "missing property", "102", 1, 1, 900, []),
            ]);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenCommand));
        receipt.ResultContract.Should().Be(AtomicUnitCsvImport.Codec.ContractName);
        AtomicUnitCsvImport.Codec.Deserialize(receipt.ResultJson!).Rows.Should().BeEquivalentTo(
            [
                new AtomicUnitImportRowResult(
                    2, true, false, unit.Id, property.Id, "101", 2, 1.5m, 1200, []),
                new AtomicUnitImportRowResult(
                    3, false, false, null, null, "102", 1, 1, 900,
                    ["Property was not found in this portfolio."]),
            ], options => options.WithStrictOrdering());

        var audit = await AuditAsync(identity, nameof(Unit));
        audit.Timestamp.Should().BeOnOrAfter(databaseBefore.AddSeconds(-1));
        audit.Timestamp.Should().BeOnOrBefore(databaseAfter.AddSeconds(1));
        audit.Timestamp.Should().NotBe(SeparatedAuditClock);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"unit-import:{operationKey}:unit:")))
            .Should().Be(1);
    }

    [Fact]
    public async Task PaymentImport_PreservesFingerprintRetryClockOutboxAndStaleAuthorization()
    {
        var scope = await SeedScopeAsync(DateTime.UtcNow, "payment");
        const string operationKey = "family-p5-payment-csv";
        const string csv =
            "relationshipNumber,propertyName,unitNumber,paymentType,amount,paidDate,method,externalReference,notes\n" +
            "LM-payment,,,Rent,875,2025-01-05,ACH,ref-payment,January rent\n" +
            "missing-relationship,,,Rent,100,2025-01-06,Cash,ref-missing,Missing tenant\n";

        CsvImportResult first;
        CsvImportResult replay;
        var databaseBefore = DateTime.UtcNow;
        await using (var services = BuildServices())
        await using (var requestScope = services.CreateAsyncScope())
        {
            var sut = requestScope.ServiceProvider.GetRequiredService<CsvImportService>();
            first = await sut.ImportAsync(scope, "payment", Csv(csv), false,
                CommandContext(scope, operationKey));
            replay = await sut.ImportAsync(scope, "payment", Csv(csv), false,
                CommandContext(scope, operationKey));

        }
        var databaseAfter = DateTime.UtcNow;

        replay.Should().BeEquivalentTo(first);
        first.TotalRows.Should().Be(2);
        first.ValidRows.Should().Be(1);
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(0);
        var ledger = await _context.Db.TenantLedgerEntries.AsNoTracking().SingleAsync(row =>
            row.PortfolioId == scope.PortfolioId
            && row.EntryType == TenantLedgerEntryType.PaymentReceipt);
        first.Rows.Should().BeEquivalentTo(
            [
                new CsvImportRowResult
                {
                    RowNumber = 2,
                    Valid = true,
                    Errors = [],
                    CreatedId = ledger.Id,
                    IsDuplicate = false,
                    SkipReason = null,
                },
                new CsvImportRowResult
                {
                    RowNumber = 3,
                    Valid = false,
                    Errors = ["Tenant account was not found."],
                    CreatedId = null,
                    IsDuplicate = false,
                    SkipReason = null,
                },
            ], options => options.WithStrictOrdering());

        var identity = new AtomicCommandIdentity(
            "payment.csv-import", $"{scope.PortfolioId}:{scope.AccessContextId}:{operationKey}");
        var receipt = await ReceiptAsync(identity);
        var rowsJson = PaymentRowsJson(scope.PortfolioId, operationKey);
        var frozenCommand = new AtomicPaymentCsvImportCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, operationKey, rowsJson);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenCommand));
        receipt.ResultContract.Should().Be(AtomicPaymentCsvImport.Codec.ContractName);
        AtomicPaymentCsvImport.Codec.Deserialize(receipt.ResultJson!).Rows
            .Should().BeEquivalentTo(
            [
                new AtomicPaymentCsvImportRowResult(2, true, false, ledger.Id,
                    ledger.TenantAccountId, []),
                new AtomicPaymentCsvImportRowResult(3, false, false, null, null,
                    ["Tenant account was not found."]),
            ], options => options.WithStrictOrdering());

        (await _context.Db.TenantPaymentAttempts.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId)).Should().Be(1);
        (await _context.Db.TenantLedgerEntries.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId
            && row.EntryType == TenantLedgerEntryType.PaymentReceipt)).Should().Be(1);
        (await _context.Db.TenantLedgerAllocations.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId)).Should().Be(0);
        (await _context.Db.JournalEntries.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId)).Should().Be(0);
        var audit = await AuditAsync(identity, nameof(TenantAccount));
        audit.Timestamp.Should().BeOnOrAfter(databaseBefore.AddSeconds(-1));
        audit.Timestamp.Should().BeOnOrBefore(databaseAfter.AddSeconds(1));
        audit.Timestamp.Should().NotBe(SeparatedAuditClock);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"payment-import:{operationKey}:tenant-account:")))
            .Should().Be(1);

        await RevokeAsync(scope, DateTime.UtcNow);
        await using var staleServices = BuildServices();
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<CsvImportService>()
            .ImportAsync(scope, "payment", Csv(csv), false,
                CommandContext(scope, operationKey));
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Workspace access changed. Refresh and try again.");
    }

    private async Task<WorkspaceReadScope> SeedScopeAsync(DateTime now, string suffix)
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = suffix switch
            {
                "unit" => "CSV unit alias property",
                "payment" => "CSV payment property",
                _ => "CSV core alias property",
            },
            AddressLine1 = $"{suffix} Executor Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, membership, assignment, session, property);
        await _context.Db.SaveChangesAsync();
        if (suffix == "payment")
        {
            var unit = new Unit
            {
                PortfolioId = 1,
                PropertyId = property.Id,
                UnitNumber = "1",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var management = new LeaseManagement
            {
                PortfolioId = 1,
                PropertyId = property.Id,
                Unit = unit,
                RelationshipNumber = "LM-payment",
                PlannedPossessionAtUtc = now.AddMonths(-1),
                CreatedAtUtc = now,
                CreatedByUserId = user.Id,
                UpdatedAtUtc = now,
                RowVersion = Guid.NewGuid(),
            };
            _context.Db.AddRange(unit, management);
            await _context.Db.SaveChangesAsync();
            _context.Db.TenantAccounts.Add(new TenantAccount
            {
                PortfolioId = 1,
                LeaseManagementId = management.Id,
                AccountNumber = "TA-payment",
                Currency = "USD",
                OpenedAtUtc = now.AddMonths(-1),
                CreatedAtUtc = now,
                CreatedByUserId = user.Id,
            });
            await _context.Db.SaveChangesAsync();
        }
        _context.Db.ChangeTracker.Clear();
        return new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(SeparatedAuditClock));
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IUnitCsvImportPreviewQuery, AtomicUnitImportPersistence>();
        services.AddScoped<ICoreCsvImportPreviewQuery, AtomicCoreCsvImportPersistence>();
        services.AddScoped<IPaymentCsvImportPreviewQuery, AtomicPaymentCsvImportPersistence>();
        services.AddScoped<CsvImportService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<AtomicCommandReceipt> ReceiptAsync(AtomicCommandIdentity identity)
    {
        _context.Db.ChangeTracker.Clear();
        return await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey);
    }

    private async Task<AtomicAuditLog> AuditAsync(
        AtomicCommandIdentity identity,
        string entityType)
    {
        _context.Db.ChangeTracker.Clear();
        return await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == entityType);
    }

    private async Task RevokeAsync(WorkspaceReadScope scope, DateTime now)
    {
        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = now;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private static CsvImportCommandContext CommandContext(
        WorkspaceReadScope scope,
        string operationKey) =>
        new(scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, operationKey);

    private static Stream Csv(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string PaymentRowsJson(int portfolioId, string operationKey) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                RowNumber = 2,
                RelationshipNumber = "LM-payment",
                PropertyName = (string?)null,
                UnitNumber = (string?)null,
                Amount = 875m,
                PaidOn = new DateOnly(2025, 1, 5),
                Method = "ACH",
                ExternalReference = "ref-payment",
                Description = "January rent",
                DeliveryKey = $"csv-receipt:{portfolioId}:{operationKey}:2",
                Errors = Array.Empty<string>(),
            },
            new
            {
                RowNumber = 3,
                RelationshipNumber = "missing-relationship",
                PropertyName = (string?)null,
                UnitNumber = (string?)null,
                Amount = 100m,
                PaidOn = new DateOnly(2025, 1, 6),
                Method = "Cash",
                ExternalReference = "ref-missing",
                Description = "Missing tenant",
                DeliveryKey = $"csv-receipt:{portfolioId}:{operationKey}:3",
                Errors = Array.Empty<string>(),
            },
        });

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:csv-import-write-executor";
        public string? IpAddress => "127.0.0.1";
    }
}
