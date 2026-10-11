using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Applications;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Applications;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class ApplicationWriteExecutorTests : IAsyncLifetime
{
    private static readonly DateTime AuditNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly AdvisoryLockRecorder _locks = new();
    private MigratedPostgreSqlTestContext _context = null!;

    public ApplicationWriteExecutorTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([_locks]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task ExecutorRules_AcquireResolvedLegacyLocks_AndReplayWithoutDuplicates()
    {
        var scope = await SeedScopeAndApplicationAsync();
        var application = await _context.Db.RentalApplications.AsNoTracking().SingleAsync();
        var beforeDatabaseClock = await DatabaseClockAsync(_context.Db);

        await using var services = BuildServices();
        await using var serviceScope = services.CreateAsyncScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<ApplicationService>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        _locks.Expect(scope);
        var updated = await service.UpdateAuthorizedAsync(
            scope, application.Id, new UpdateApplicationRequest { FirstName = "Executor" },
            scope.UserId, "application-update-retry");
        var updatedReplay = await service.UpdateAuthorizedAsync(
            scope, application.Id, new UpdateApplicationRequest { FirstName = "Executor" },
            scope.UserId, "application-update-retry");
        updatedReplay.Should().BeEquivalentTo(updated);
        _locks.Take().Should().Equal(
            $"AuthSession:{scope.SessionId}",
            $"WorkspaceAccessContext:{scope.AccessContextId}",
            "Portfolio:1",
            $"RentalApplication:{application.Id}");

        const string publicKey = "public-submit-retry";
        var publicRequest = new SubmitApplicationRequest
        {
            FirstName = "Public",
            LastName = "Replay",
            Email = "public-replay@example.test",
            ConsentGiven = true,
        };
        var publicFirst = await service.SubmitAsync(
            "executor-public-token", publicRequest, "198.51.100.9", publicKey);
        var publicReplay = await service.SubmitAsync(
            "executor-public-token", publicRequest, "198.51.100.9", publicKey);
        publicReplay.Should().BeEquivalentTo(publicFirst);
        _locks.Take().Should().Equal("Portfolio:1");

        var record = RecordFee(scope, application.Id, "record-fee-retry");
        var recordWrite = ApplicationFinanceWriteSupport.Write(record,
            serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        var recordFirst = await writes.ExecuteAsync(record.IdempotencyKey, recordWrite);
        var recordReplay = await writes.ExecuteAsync(record.IdempotencyKey, recordWrite);
        recordReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        recordReplay.Value.Should().BeEquivalentTo(recordFirst.Value);
        _locks.Take().Should().Equal($"RentalApplication:{application.Id}");

        var refund = RefundFee(
            scope, application.Id, recordFirst.Value.EntryId!.Value, "refund-fee-retry");
        var refundWrite = ApplicationFinanceWriteSupport.Write(refund,
            serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        var refundFirst = await writes.ExecuteAsync(refund.IdempotencyKey, refundWrite);
        var refundReplay = await writes.ExecuteAsync(refund.IdempotencyKey, refundWrite);
        refundReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        refundReplay.Value.Should().BeEquivalentTo(refundFirst.Value);
        _locks.Take().Should().Equal($"RentalApplication:{application.Id}");

        var afterDatabaseClock = await DatabaseClockAsync(_context.Db);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.RentalApplications.CountAsync()).Should().Be(2);
        (await _context.Db.ApplicationFinancialEntries.CountAsync()).Should().Be(2);
        (await _context.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == "application-update-retry:entity")).Should().Be(1);
        (await _context.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == record.IdempotencyKey)).Should().Be(1);
        (await _context.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == refund.IdempotencyKey)).Should().Be(1);

        var persistedPublic = await _context.Db.RentalApplications.AsNoTracking()
            .SingleAsync(row => row.Email == publicRequest.Email);
        persistedPublic.CreatedAt.Should().BeOnOrAfter(beforeDatabaseClock)
            .And.BeOnOrBefore(afterDatabaseClock);
        var feeEntry = await _context.Db.ApplicationFinancialEntries.AsNoTracking()
            .SingleAsync(row => row.Id == recordFirst.Value.EntryId);
        feeEntry.OccurredAtUtc.Should().BeOnOrAfter(beforeDatabaseClock)
            .And.BeOnOrBefore(afterDatabaseClock);
        var financeAudits = await _context.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == "application-finance.record-fee"
                || row.CommandType == "application-finance.refund-fee")
            .ToListAsync();
        financeAudits.Should().HaveCount(3);
        financeAudits.Should().ContainSingle(row =>
            row.CommandType == "application-finance.record-fee"
            && row.EntityType == nameof(ApplicationFinancialAccount)
            && row.EntityId == recordFirst.Value.AccountId
            && row.ChangeReason == "Opened the application's pre-tenancy financial account.");
        financeAudits.Should().ContainSingle(row =>
            row.CommandType == "application-finance.record-fee"
            && row.EntityType == nameof(ApplicationFinancialEntry)
            && row.EntityId == recordFirst.Value.EntryId
            && row.ChangeReason == "Recorded an append-only application fee collection.");
        financeAudits.Should().ContainSingle(row =>
            row.CommandType == "application-finance.refund-fee"
            && row.EntityType == nameof(ApplicationFinancialEntry)
            && row.EntityId == refundFirst.Value.EntryId
            && row.ChangeReason == "Recorded an append-only application fee refund.");
        var saveTimeAudits = await _context.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == "application.public-submit"
                || row.CommandType == "application-finance.record-fee"
                || row.CommandType == "application-finance.refund-fee")
            .Select(row => row.Timestamp)
            .ToListAsync();
        saveTimeAudits.Should().NotBeEmpty().And.OnlyContain(timestamp => timestamp == AuditNow);
        saveTimeAudits.Should().OnlyContain(timestamp => timestamp != feeEntry.OccurredAtUtc);
    }

    [Fact]
    public async Task FrozenLegacyReceipts_ReplayThroughExecutor_WithoutNewRows()
    {
        var scope = await SeedScopeAndApplicationAsync();
        var application = await _context.Db.RentalApplications.AsNoTracking().SingleAsync();
        var scopedRequest = new UpdateApplicationRequest { FirstName = "Stored" };
        var scoped = AtomicRentalMutation.Command(
            scope, AtomicRentalMutationDomain.Application, AtomicRentalMutationOperation.Update,
            application.Id, "legacy-application ", scopedRequest);
        var publicRequest = new SubmitApplicationRequest
            { FirstName = "Stored", LastName = "Public", ConsentGiven = true };
        var submitted = new AtomicPublicApplicationSubmissionCommand(
            "executor-public-token", JsonSerializer.Serialize(publicRequest),
            "198.51.100.10", "legacy-public ");
        var record = RecordFee(scope, application.Id, "legacy-record");
        var refund = RefundFee(scope, application.Id, 991, "legacy-refund");

        var scopedStored = new AtomicRentalMutationResult(true, true, application.Id,
            ResponseJson: JsonSerializer.Serialize(new ApplicationResponse
                { Id = application.Id, PortfolioId = 1, FirstName = "Stored" }));
        var publicStored = new AtomicPublicApplicationSubmissionResult(
            true, 992, "Submitted", "Stored public result");
        var feeStored = new ApplicationFinanceMutationResult(
            ApplicationFinanceMutationOutcome.Posted, application.Id, 993, 994, null,
            ApplicationFinancialEntryType.FeeCollection, ApplicationFinancialDirection.Increase,
            40m, "USD", new DateOnly(2099, 8, 21), AuditNow, true, null);

        SeedReceipt(AtomicRentalMutation.Identity(scoped), scoped,
            AtomicRentalMutation.Codec.ContractName, AtomicRentalMutation.Codec.Serialize(scopedStored));
        SeedReceipt(AtomicPublicApplicationSubmission.Identity(submitted), submitted,
            AtomicPublicApplicationSubmission.Codec.ContractName,
            AtomicPublicApplicationSubmission.Codec.Serialize(publicStored));
        SeedReceipt(new AtomicCommandIdentity("application-finance.record-fee", record.IdempotencyKey),
            record, ApplicationFinanceWriteSupport.ResultContract, JsonSerializer.Serialize(feeStored));
        SeedReceipt(new AtomicCommandIdentity("application-finance.refund-fee", refund.IdempotencyKey),
            refund, ApplicationFinanceWriteSupport.ResultContract, JsonSerializer.Serialize(feeStored));
        await _context.Db.SaveChangesAsync();
        var applicationsBefore = await _context.Db.RentalApplications.CountAsync();
        var entriesBefore = await _context.Db.ApplicationFinancialEntries.CountAsync();
        var outboxBefore = await _context.Db.OutboxMessages.CountAsync();
        var receiptsBefore = await _context.Db.AtomicCommandReceipts.CountAsync();
        var auditsBefore = await _context.Db.AtomicAuditLogs.CountAsync();

        await using var services = BuildServices();
        await using var serviceScope = services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var service = serviceScope.ServiceProvider.GetRequiredService<ApplicationService>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        (await service.UpdateAuthorizedAsync(scope, application.Id, scopedRequest,
            scope.UserId, "legacy-application ")).Should().NotBeNull();
        (await service.SubmitAsync("executor-public-token", publicRequest,
            "198.51.100.10", "legacy-public ")).Should().BeEquivalentTo(new SubmitApplicationResult
            {
                ApplicationId = publicStored.ApplicationId,
                Status = publicStored.Status,
                Message = publicStored.Message,
            });
        (await writes.ExecuteAsync(record.IdempotencyKey,
            ApplicationFinanceWriteSupport.Write(record, db))).Value.Should().BeEquivalentTo(feeStored);
        (await writes.ExecuteAsync(refund.IdempotencyKey,
            ApplicationFinanceWriteSupport.Write(refund, db))).Value.Should().BeEquivalentTo(feeStored);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.RentalApplications.CountAsync()).Should().Be(applicationsBefore);
        (await _context.Db.RentalApplications.SingleAsync(row => row.Id == application.Id))
            .FirstName.Should().Be("Existing");
        (await _context.Db.ApplicationFinancialEntries.CountAsync()).Should().Be(entriesBefore);
        (await _context.Db.OutboxMessages.CountAsync()).Should().Be(outboxBefore);
        (await _context.Db.AtomicCommandReceipts.CountAsync()).Should().Be(receiptsBefore);
        (await _context.Db.AtomicAuditLogs.CountAsync()).Should().Be(auditsBefore);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(AuditNow));
        services.AddSingleton(Mock.Of<IFileStorage>());
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddSingleton(Mock.Of<IAuditTrailService>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<ApplicationService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .AddInterceptors(_locks)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<WorkspaceReadScope> SeedScopeAndApplicationAsync()
    {
        var now = DateTime.UtcNow;
        var portfolio = await _context.Db.Portfolios.SingleAsync(row => row.Id == 1);
        portfolio.PublicApplicationToken = "executor-public-token";
        var access = new WorkspaceAccessContext
        {
            UserId = 1, PortfolioId = 1, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = access, PortfolioId = 1, Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = 1, ActiveAccessContext = access,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            PortfolioId = 1, Name = "Application executor property", AddressLine1 = "5 Write Way",
            City = "Columbus", State = "OH", PostalCode = "43215",
            CreatedAt = now, UpdatedAt = now,
        };
        var application = new RentalApplication
        {
            PortfolioId = 1, Property = property, FirstName = "Existing", LastName = "Applicant",
            Email = "existing-application@example.test", Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now, CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.AddRange(assignment, session, application);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new(1, 1, session.Id, access.Id, access.AccessRevision);
    }

    private void SeedReceipt<TCommand>(
        AtomicCommandIdentity identity, TCommand command, string contract, string resultJson)
        where TCommand : notnull, IAtomicCommandData =>
        _context.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = identity.CommandType,
            IdempotencyKey = identity.IdempotencyKey,
            RequestFingerprint = AtomicCommandFingerprint.Create(command),
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract,
            ResultJson = resultJson, StartedAt = AuditNow, CompletedAt = AuditNow,
        });

    private static RecordApplicationFeeCommand RecordFee(
        WorkspaceReadScope scope, int applicationId, string key) => new(
            1, applicationId, 40m, "USD", null, "Card", null, null,
            ApplicationFinancialEntrySource.Manual, null, key, scope.UserId,
            scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static RefundApplicationFeeCommand RefundFee(
        WorkspaceReadScope scope, int applicationId, int collectionId, string key) => new(
            1, applicationId, collectionId, 10m, null, "Card", null, null,
            ApplicationFinancialEntrySource.Manual, null, "Applicant withdrew", key, scope.UserId,
            scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static Task<DateTime> DatabaseClockAsync(RentalCommandDbContext db) =>
        db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:application-write-executor";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class AdvisoryLockRecorder : DbCommandInterceptor
    {
        private readonly List<(int Count, long First, int? Second)> _recorded = [];
        private WorkspaceReadScope? _scope;

        public void Expect(WorkspaceReadScope scope) => _scope = scope;

        public string[] Take()
        {
            var scope = _scope ?? throw new InvalidOperationException("A lock scope was not supplied.");
            var labels = _recorded.Select(item => item.Count == 1
                    ? item.First == GuidLock("AuthSession", scope.SessionId)
                        ? $"AuthSession:{scope.SessionId}"
                        : $"UnknownGuid:{item.First}"
                    : $"{Namespace(item.First)}:{item.Second}")
                .ToArray();
            _recorded.Clear();
            return labels;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                var values = command.Parameters.Cast<DbParameter>()
                    .Select(parameter => Convert.ToInt64(parameter.Value)).ToArray();
                _recorded.Add((values.Length, values[0],
                    values.Length == 2 ? Convert.ToInt32(values[1]) : null));
            }
            return ValueTask.FromResult(result);
        }

        private static string Namespace(long key) => key switch
        {
            var value when value == NamespaceKey("WorkspaceAccessContext") => "WorkspaceAccessContext",
            var value when value == NamespaceKey("Portfolio") => "Portfolio",
            var value when value == NamespaceKey("RentalApplication") => "RentalApplication",
            _ => $"Unknown:{key}",
        };

        private static int NamespaceKey(string value) =>
            0x52434D44 ^ BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private static long GuidLock(string lockNamespace, Guid id)
        {
            var input = new byte[48];
            SHA256.HashData(Encoding.UTF8.GetBytes(lockNamespace)).CopyTo(input, 0);
            id.TryWriteBytes(input.AsSpan(32));
            return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(input));
        }
    }
}
