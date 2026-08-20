using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Import;
using RentalCommand.Api.Writes;
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

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
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
    public async Task CoreImport_PreservesFingerprintRetryPartialBatchClockOutboxAndStaleAuthorization()
    {
        var scope = await SeedScopeAsync(DateTime.UtcNow, "core");
        const string operationKey = "family-p4-core-csv";
        const string csv =
            "firstName,lastName,email,phone\n" +
            "Ada,Importer,ada-importer@example.test,555-0100\n" +
            "Bad,Email,not-an-email,555-0101\n";

        CsvImportResult first;
        CsvImportResult replay;
        var databaseBefore = DateTime.UtcNow;
        await using (var services = BuildServices())
        await using (var requestScope = services.CreateAsyncScope())
        {
            var sut = requestScope.ServiceProvider.GetRequiredService<CsvImportService>();
            first = await sut.ImportAsync(scope, "tenant", Csv(csv), false,
                CommandContext(scope, operationKey));
            replay = await sut.ImportAsync(scope, "tenant", Csv(csv), false,
                CommandContext(scope, operationKey));
        }
        var databaseAfter = DateTime.UtcNow;

        replay.Should().BeEquivalentTo(first);
        first.TotalRows.Should().Be(2);
        first.CreatedRows.Should().Be(1);
        first.ValidRows.Should().Be(1);
        first.DuplicateRows.Should().Be(0);
        first.Rows.Should().ContainSingle(row => !row.Valid);
        (await _context.Db.Tenants.AsNoTracking().CountAsync(row =>
            row.Email == "ada-importer@example.test")).Should().Be(1);

        var identity = new AtomicCommandIdentity(
            "tenant.csv-import", $"{scope.PortfolioId}:{scope.AccessContextId}:{operationKey}");
        var receipt = await ReceiptAsync(identity);
        var rowsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RowNumber = 2, FirstName = "Ada", LastName = "Importer",
                Email = "ada-importer@example.test", Phone = "555-0100",
                Errors = Array.Empty<string>(),
            },
            new
            {
                RowNumber = 3, FirstName = "Bad", LastName = "Email",
                Email = "not-an-email", Phone = "555-0101",
                Errors = new[] { "The Email field is not a valid e-mail address." },
            },
        });
        var frozenCommand = new AtomicCoreCsvImportCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, AtomicCoreCsvImportDomain.Tenant, operationKey, rowsJson);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenCommand));
        receipt.ResultContract.Should().Be(AtomicCoreCsvImport.Codec.ContractName);

        var audit = await AuditAsync(identity, nameof(Tenant));
        audit.Timestamp.Should().BeOnOrAfter(databaseBefore.AddSeconds(-1));
        audit.Timestamp.Should().BeOnOrBefore(databaseAfter.AddSeconds(1));
        audit.Timestamp.Should().NotBe(SeparatedAuditClock);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"tenant-import:{operationKey}:tenant:")))
            .Should().Be(1);

        await RevokeAsync(scope, DateTime.UtcNow);
        await using var staleServices = BuildServices();
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<CsvImportService>()
            .ImportAsync(scope, "tenant", Csv(csv), false,
                CommandContext(scope, operationKey));
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Workspace access changed. Refresh and try again.");
    }

    [Fact]
    public async Task UnitImport_PreservesAliasPartialBatchFingerprintRetryClockOutboxAndRetiresLegacyPaths()
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

            var oldCore = new AtomicCoreCsvImportCommand(
                scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, AtomicCoreCsvImportDomain.Tenant,
                "retired-core", "[{}]");
            Func<Task> retiredCore = () => requestScope.ServiceProvider
                .GetRequiredService<IAtomicUnitOfWork>()
                .ExecuteAsync(AtomicCoreCsvImport.Identity(oldCore), oldCore,
                    AtomicCoreCsvImport.Codec);
            await retiredCore.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Core CSV imports must use the shared request write executor.");

            var oldUnit = new AtomicUnitCsvImportCommand(
                scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, "retired-unit",
                [new AtomicUnitImportRow(2, property.Id, null, "retired", 1, 1, 1, [])]);
            Func<Task> retiredUnit = () => requestScope.ServiceProvider
                .GetRequiredService<IAtomicUnitOfWork>()
                .ExecuteAsync(AtomicUnitCsvImport.Identity(oldUnit), oldUnit,
                    AtomicUnitCsvImport.Codec);
            await retiredUnit.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Unit CSV imports must use the shared request write executor.");
        }
        var databaseAfter = DateTime.UtcNow;

        replay.Should().BeEquivalentTo(first);
        first.TotalRows.Should().Be(2);
        first.ValidRows.Should().Be(1);
        first.CreatedRows.Should().Be(1);
        first.Rows[0].CreatedId.Should().BePositive();
        first.Rows[0].Valid.Should().BeTrue();
        first.Rows[1].Valid.Should().BeFalse();
        first.Rows[1].Errors.Should().ContainMatch("*not found*");
        (await _context.Db.Units.AsNoTracking().CountAsync(row =>
            row.PropertyId == property.Id && row.UnitNumber == "101")).Should().Be(1);

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

        var audit = await AuditAsync(identity, nameof(Unit));
        audit.Timestamp.Should().BeOnOrAfter(databaseBefore.AddSeconds(-1));
        audit.Timestamp.Should().BeOnOrBefore(databaseAfter.AddSeconds(1));
        audit.Timestamp.Should().NotBe(SeparatedAuditClock);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"unit-import:{operationKey}:unit:")))
            .Should().Be(1);
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
            Name = suffix == "unit" ? "CSV unit alias property" : "CSV core property",
            AddressLine1 = $"{suffix} Executor Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, membership, assignment, session, property);
        await _context.Db.SaveChangesAsync();
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
        services.AddAtomicCommandHandler<
            AtomicCoreCsvImportCommand, AtomicCoreCsvImportResult,
            AtomicCoreCsvImportHandler>();
        services.AddAtomicCommandHandler<
            AtomicUnitCsvImportCommand, AtomicUnitCsvImportResult,
            AtomicUnitCsvImportHandler>();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
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
