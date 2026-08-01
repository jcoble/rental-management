using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class OpeningSecurityDepositRecoveryPostgreSqlTests
{
    private static readonly AtomicJsonResultCodec<RecoverOpeningSecurityDepositsResult> Codec =
        new("opening-security-deposits.recover.v1");
    private static readonly DateOnly OpeningDate = new(2027, 1, 1);

    private readonly MigratedPostgreSqlFixture _fixture;

    public OpeningSecurityDepositRecoveryPostgreSqlTests(
        MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Recovery_ValidatesControl_Authorizes_RollsBack_Replays_AndNeverInventsTenantPayments()
    {
        var failure = new OutboxFailureInterceptor();
        var commands = new CommandCaptureInterceptor();
        await using var setup = await _fixture.CreateContextAsync([failure, commands]);
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(
            1,
            nameof(Recovery_ValidatesControl_Authorizes_RollsBack_Replays_AndNeverInventsTenantPayments));
        await SeedOpeningAgreementAsync(setup.Db, scope.UserId, 1_000m, "one");
        await SeedOpeningAgreementAsync(setup.Db, scope.UserId, 1_500m, "two");
        setup.Db.ChangeTracker.Clear();

        await using var services = Services(setup.ConnectionString, failure, commands);
        await using var atomicScope = services.CreateAsyncScope();
        var atomic = atomicScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();

        var wrongControl = Command(scope, "wrong-control", expectedCount: 2, expectedTotal: 2_499m);
        await FluentActions.Invoking(() => atomic.ExecuteAsync(
                Identity(wrongControl),
                wrongControl,
                Codec))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*total does not match*");
        await AssertNoOpeningMutationAsync(setup.Db);

        var denied = Command(
            scope with { AccessRevision = scope.AccessRevision + 1 },
            "denied",
            expectedCount: 2,
            expectedTotal: 2_500m);
        await FluentActions.Invoking(() => atomic.ExecuteAsync(
                Identity(denied),
                denied,
                Codec))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertNoOpeningMutationAsync(setup.Db);

        var rollback = Command(scope, "rollback", expectedCount: 2, expectedTotal: 2_500m);
        failure.FailNextOutboxInsert = true;
        await FluentActions.Invoking(() => atomic.ExecuteAsync(
                Identity(rollback),
                rollback,
                Codec))
            .Should().ThrowAsync<Exception>();
        await AssertNoOpeningMutationAsync(setup.Db);

        commands.Commands.Clear();
        var command = Command(scope, "success", expectedCount: 2, expectedTotal: 2_500m);
        var first = await atomic.ExecuteAsync(Identity(command), command, Codec);
        var replay = await atomic.ExecuteAsync(Identity(command), command, Codec);
        var laterSweep = Command(scope, "later-sweep", expectedCount: 2, expectedTotal: 2_500m);
        var sweep = await atomic.ExecuteAsync(Identity(laterSweep), laterSweep, Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Should().Be(new RecoverOpeningSecurityDepositsResult(
            2, 2, 2, 2_500m, "FIN-00001"));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        sweep.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        sweep.Value.CreatedAccountCount.Should().Be(0);
        sweep.Value.CreatedEntryCount.Should().Be(0);
        sweep.Value.AccountCount.Should().Be(2);
        sweep.Value.ReconciledTotal.Should().Be(2_500m);

        setup.Db.ChangeTracker.Clear();
        var exactRows = await (
                from deposit in setup.Db.SecurityDepositAccounts.AsNoTracking()
                join entry in setup.Db.SecurityDepositEntries.AsNoTracking()
                    on new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
                    equals new { entry.PortfolioId, entry.SecurityDepositAccountId }
                where deposit.PortfolioId == 1
                    && entry.BusinessKey.StartsWith("opening-deposit:FIN-00001:account:")
                orderby deposit.TenantAccountId
                select new
                {
                    deposit.TenantAccountId,
                    deposit.OriginatingAgreementId,
                    entry.LeaseAgreementId,
                    entry.EntryType,
                    entry.Direction,
                    entry.Amount,
                    entry.EffectiveOn,
                    entry.TenantLedgerEntryId,
                })
            .ToListAsync();
        exactRows.Should().HaveCount(2);
        exactRows.Should().OnlyContain(row =>
            row.OriginatingAgreementId == row.LeaseAgreementId
            && row.EntryType == SecurityDepositEntryType.Receipt
            && row.Direction == SecurityDepositDirection.Increase
            && row.EffectiveOn == OpeningDate
            && row.TenantLedgerEntryId == null);
        exactRows.Select(row => row.Amount).Should().BeEquivalentTo([1_000m, 1_500m]);

        var openingJournals = await setup.Db.JournalEntries.AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.LedgerAccount)
            .Where(entry => entry.PortfolioId == 1
                && entry.SourceType == JournalSourceType.SecurityDepositReceipt)
            .OrderBy(entry => entry.SourceId)
            .ToListAsync();
        openingJournals.Should().HaveCount(2);
        openingJournals.Should().OnlyContain(entry => entry.Lines.Count == 2);
        openingJournals.SelectMany(entry => entry.Lines).Should().OnlyContain(line =>
            line.LedgerAccount!.SystemKey == "security-deposit-trust-cash"
                || line.LedgerAccount.SystemKey == "tenant-security-deposits-payable");
        openingJournals.SelectMany(entry => entry.Lines)
            .Should().NotContain(line => line.LedgerAccount!.AccountType == AccountType.Income);

        var projected = await setup.Db.SecurityDepositBalanceProjections.AsNoTracking()
            .Where(row => row.PortfolioId == 1)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Held = group.Sum(row => row.HeldBalance),
                Received = group.Sum(row => row.TotalReceived),
            })
            .SingleAsync();
        projected.Count.Should().Be(2);
        projected.Held.Should().Be(2_500m);
        projected.Received.Should().Be(2_500m);

        (await setup.Db.TenantPaymentAttempts.CountAsync()).Should().Be(0,
            "an opening position is not a newly collected payment");
        (await setup.Db.TenantLedgerEntries.CountAsync()).Should().Be(0,
            "an opening deposit must not invent a tenant charge or receipt");
        (await setup.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == Identity(command).CommandType
            && row.CommandIdempotencyKey == Identity(command).IdempotencyKey
            && row.EntityType == nameof(Portfolio)
            && row.EntityId == 1)).Should().Be(1);
        (await setup.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "opening-security-deposits",
                "1:FIN-00001"))).Should().Be(1);

        commands.Commands.Count(sql =>
                sql.Contains("WITH candidates AS MATERIALIZED", StringComparison.Ordinal)
                && sql.Contains("INSERT INTO \"SecurityDepositAccounts\"", StringComparison.Ordinal)
                && sql.Contains("INSERT INTO \"SecurityDepositEntries\"", StringComparison.Ordinal))
            .Should().Be(2,
                "the successful mutation and later no-op sweep each use one set-based statement; receipt replay executes none");
        commands.Commands.Should().Contain(sql =>
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("sum", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("vw_security_deposit_balances", StringComparison.OrdinalIgnoreCase),
            "held cash and liability proof must aggregate in PostgreSQL");
    }

    private static ServiceProvider Services(
        string connectionString,
        OutboxFailureInterceptor failure,
        CommandCaptureInterceptor commands)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(failure);
        services.AddSingleton(commands);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            RecoverOpeningSecurityDepositsCommand,
            RecoverOpeningSecurityDepositsResult,
            RecoverOpeningSecurityDepositsHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .AddInterceptors(provider.GetRequiredService<OutboxFailureInterceptor>())
                .AddInterceptors(provider.GetRequiredService<CommandCaptureInterceptor>())
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static AtomicCommandIdentity Identity(
        RecoverOpeningSecurityDepositsCommand command) =>
        new("opening-security-deposits.recover", command.DeliveryIdempotencyKey);

    private static RecoverOpeningSecurityDepositsCommand Command(
        WorkspaceReadScope scope,
        string key,
        int expectedCount,
        decimal expectedTotal) =>
        new(
            scope.PortfolioId,
            OpeningDate,
            expectedCount,
            expectedTotal,
            "FIN-00001",
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            CapabilityKeys.MoneyDepositsManage,
            $"opening-security-deposits:{scope.PortfolioId}:{key}");

    private static async Task AssertNoOpeningMutationAsync(RentalCommandDbContext db)
    {
        db.ChangeTracker.Clear();
        (await db.SecurityDepositAccounts.CountAsync()).Should().Be(0);
        (await db.SecurityDepositEntries.CountAsync()).Should().Be(0);
        (await db.TenantPaymentAttempts.CountAsync()).Should().Be(0);
        (await db.TenantLedgerEntries.CountAsync()).Should().Be(0);
    }

    private static async Task SeedOpeningAgreementAsync(
        RentalCommandDbContext db,
        int actorUserId,
        decimal deposit,
        string suffix)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Opening Deposit {suffix}",
            AddressLine1 = $"{suffix} Deposit Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = $"OD-{suffix}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Opening",
            LastName = suffix,
            Email = $"opening-{suffix}-{Guid.NewGuid():N}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, unit, tenant);
        await db.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-OD-{suffix}-{Guid.NewGuid():N}"[..18],
            PossessionGivenAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = $"TA-OD-{suffix}-{Guid.NewGuid():N}"[..18],
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
        db.AddRange(relationship, account);
        await db.SaveChangesAsync();

        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            ChangeReason = "Opening deposit test",
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"opening-deposit-source:{suffix}:{Guid.NewGuid():N}",
            RendererKey = $"opening-deposit-{suffix}",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
        var issuedFile = StoredFile($"issued-{suffix}", now);
        var executedFile = StoredFile($"executed-{suffix}", now);
        db.AddRange(party, source, issuedFile, executedFile);
        await db.SaveChangesAsync();

        var issuedArtifact = Artifact(
            issuedFile, LegalDocumentArtifactKind.IssuedAgreement, actorUserId, now, true);
        var executedArtifact = Artifact(
            executedFile, LegalDocumentArtifactKind.ExecutedAgreement, actorUserId, now, false);
        db.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-OD-{suffix}-{Guid.NewGuid():N}"[..18],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2027, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1),
            BaseRentAmount = deposit,
            RentDueDay = 1,
            SecurityDepositObligation = deposit,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = source.Id,
            CreatedAtUtc = now.AddDays(-3),
            CreatedByUserId = actorUserId,
            UpdatedAtUtc = now,
        };
        var signer = new LeaseAgreementSigner
        {
            PortfolioId = 1,
            LeaseAgreement = agreement,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        };
        db.AddRange(agreement, signer);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now.AddDays(-2);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now.AddDays(-1);
        agreement.UpdatedAtUtc = now;
        await db.SaveChangesAsync();
    }

    private static StoredFile StoredFile(string suffix, DateTime now) => new()
    {
        PortfolioId = 1,
        FileName = $"{suffix}.pdf",
        FilePath = $"test/{Guid.NewGuid():N}/{suffix}.pdf",
        ContentType = "application/pdf",
        FileSize = 12,
        ContentSha256 = new string(suffix.StartsWith("issued", StringComparison.Ordinal) ? 'a' : 'b', 64),
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        int actorId,
        DateTime now,
        bool issued) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = $"test/{Guid.NewGuid():N}/{file.FileName}",
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = issued ? new string('c', 64) : new string('d', 64),
        LegalIssuanceFingerprint = issued ? new string('e', 64) : null,
        CreatedAtUtc = now,
        CreatedByUserId = actorId,
    };

    private sealed class OutboxFailureInterceptor : DbCommandInterceptor
    {
        public bool FailNextOutboxInsert { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains(
                    "INSERT INTO \"OutboxMessages\"",
                    StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedOutboxFailure : Exception;

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:opening-security-deposit-recovery";
        public string? IpAddress => "127.0.0.1";
    }
}
