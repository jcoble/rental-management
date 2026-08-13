using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Esign;
using RentalCommand.Data.Leasing;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

public sealed class NativeEsignDepositChargePostgreSqlTests : IAsyncLifetime
{
    private const int ActorUserId = 19_300;
    private static readonly DateTime FrozenBusinessNow =
        new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<ReconcileNativeEsignAgreementFinancialsResult>
        ReconcileCodec = new("native-esign.agreement-financials.reconcile.v1");
    private static readonly AtomicJsonResultCodec<ReconcileNativeEsignAgreementFinancialsBatchResult>
        ReconcileBatchCodec = new("native-esign.agreement-financials.batch-reconcile.v1");
    private static readonly AtomicJsonResultCodec<NativeSignerActionResult>
        SignCodec = new("native-esign.sign.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private IServiceScope? _serviceScope;
    private readonly DepositBatchCommandCounter _commandCounter = new();
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_esign_deposit")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            ReconcileNativeEsignAgreementFinancialsCommand,
            ReconcileNativeEsignAgreementFinancialsResult,
            ReconcileNativeEsignAgreementFinancialsHandler>();
        services.AddAtomicCommandHandler<
            ReconcileNativeEsignAgreementFinancialsBatchCommand,
            ReconcileNativeEsignAgreementFinancialsBatchResult,
            ReconcileNativeEsignAgreementFinancialsBatchHandler>();
        services.AddAtomicCommandHandler<
            RecordNativeSignatureCommand,
            NativeSignerActionResult,
            RecordNativeSignatureHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .AddInterceptors(_commandCounter)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _serviceScope = _services.CreateScope();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await FreezeClockAsync(db);
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task AppliedInitialExecution_PostsExactlyOneDepositChargeOnFrozenBusinessDate()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("applied", 1_675m);

        var transition = await ExecuteTransitionAsync(scenario);
        transition.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);

        await using var db = NewContext();
        var charge = await db.TenantLedgerEntries.SingleAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.LeaseAgreementId == scenario.LeaseAgreementId
            && entry.EntryType == TenantLedgerEntryType.DepositCharge);
        charge.Direction.Should().Be(TenantLedgerDirection.Debit);
        charge.Amount.Should().Be(1_675m);
        charge.Currency.Should().Be("USD");
        charge.EffectiveOn.Should().Be(DateOnly.FromDateTime(FrozenBusinessNow));
        charge.DueOn.Should().Be(DateOnly.FromDateTime(FrozenBusinessNow));
        charge.PostedAtUtc.Should().Be(FrozenBusinessNow);
        charge.BusinessKey.Should().Be($"security-deposit:agreement:{scenario.AgreementPublicId}");

        var journalLines = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == scenario.PortfolioId
                && line.JournalEntry.SourceType == JournalSourceType.TenantCharge
                && line.JournalEntry.SourceId == charge.Id)
            .OrderBy(line => line.LedgerAccount!.Code)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey,
                line.DebitAmount,
                line.CreditAmount,
                line.TenantAccountId,
            })
            .ToListAsync();
        journalLines.Should().BeEquivalentTo([
            new
            {
                SystemKey = "tenant-accounts-receivable",
                DebitAmount = 1_675m,
                CreditAmount = 0m,
                TenantAccountId = (int?)scenario.TenantAccountId,
            },
            new
            {
                SystemKey = "tenant-security-deposits-payable",
                DebitAmount = 0m,
                CreditAmount = 1_675m,
                TenantAccountId = (int?)scenario.TenantAccountId,
            },
        ]);
    }

    [SkippableFact]
    public async Task CompletedAgreementReconciliation_IsIdempotentAndDoesNotChangeArtifacts()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("completed-reconcile", 1_200m, completed: true);

        var first = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.agreement-financials.reconcile", "first"),
            new ReconcileNativeEsignAgreementFinancialsCommand(
                scenario.SignatureRequestId, scenario.SignatureRequestPublicId),
            ReconcileCodec);
        var second = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.agreement-financials.reconcile", "second"),
            new ReconcileNativeEsignAgreementFinancialsCommand(
                scenario.SignatureRequestId, scenario.SignatureRequestPublicId),
            ReconcileCodec);

        first.Value.DepositChargeCount.Should().Be(1);
        second.Value.DepositChargeCount.Should().Be(0);

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.BusinessKey == $"security-deposit:agreement:{scenario.AgreementPublicId}"))
            .Should().Be(1);
        var request = await db.SignatureRequests.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.SignatureRequestId);
        request.ExecutedArtifactId.Should().Be(scenario.ExecutedArtifactId);
        request.Status.Should().Be(SignatureRequestStatus.Completed);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(TenantAccount)
            && row.EntityId == scenario.TenantAccountId
            && row.ChangeReason == "Posted initial security-deposit charge at Agreement execution."))
            .Should().Be(1);
        (await CountDepositChargeOutboxMessagesAsync(
            db, [scenario.TenantAccountId]))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task CompletedAgreementBatchReconciliation_IsSetBasedAuditedAndReplayInsertsZero()
    {
        SkipIfNoDocker();
        var firstScenario = await SeedScenarioAsync("completed-batch-a", 1_100m, completed: true);
        var secondScenario = await SeedScenarioAsync("completed-batch-b", 1_300m, completed: true);
        _ = await SeedScenarioAsync("completed-batch-zero", 0m, completed: true);

        _commandCounter.Reset();
        var first = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "native-esign.agreement-financials.batch-reconcile",
                "batch-first"),
            new ReconcileNativeEsignAgreementFinancialsBatchCommand(Guid.NewGuid(), 10),
            ReconcileBatchCodec);
        var mutationCommandCount = _commandCounter.DepositBatchMutationCommandCount;
        var second = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "native-esign.agreement-financials.batch-reconcile",
                "batch-second"),
            new ReconcileNativeEsignAgreementFinancialsBatchCommand(Guid.NewGuid(), 10),
            ReconcileBatchCodec);

        first.Value.DepositChargeCount.Should().Be(2);
        mutationCommandCount.Should().Be(1);
        second.Value.DepositChargeCount.Should().Be(0);

        await using var db = NewContext();
        var accountIds = new[] { firstScenario.TenantAccountId, secondScenario.TenantAccountId };
        (await db.TenantLedgerEntries.CountAsync(entry =>
            accountIds.Contains(entry.TenantAccountId)
            && entry.EntryType == TenantLedgerEntryType.DepositCharge))
            .Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(TenantAccount)
            && accountIds.Contains(row.EntityId)
            && row.ChangeReason == "Posted initial security-deposit charge at Agreement execution."))
            .Should().Be(2);
        (await CountDepositChargeOutboxMessagesAsync(
            db, [firstScenario.TenantAccountId, secondScenario.TenantAccountId]))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task ZeroObligation_DoesNotPostDepositCharge()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("zero", 0m);

        (await ExecuteTransitionAsync(scenario)).Outcome.Should()
            .Be(AtomicLegalExecutionTransitionOutcome.Applied);

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.DepositCharge))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task RollbackAndTargetConflict_DoNotLeaveDepositCharge()
    {
        SkipIfNoDocker();
        var rollback = await SeedScenarioAsync("rollback", 900m);

        await using (var db = NewContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var attemptId = Guid.NewGuid();
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.transition.rollback", Guid.NewGuid().ToString("N")),
                attemptId,
                db);
            (await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
                db,
                commandContext,
                rollback.PortfolioId,
                rollback.LeaseManagementId,
                rollback.LeaseAgreementId,
                null,
                rollback.ExecutedArtifactId,
                FrozenBusinessNow)).Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
            await transaction.RollbackAsync();
            commandContext.EndAttempt();
        }

        var conflict = await SeedScenarioAsync("conflict", 800m, voided: true);
        (await ExecuteTransitionAsync(conflict)).Outcome.Should()
            .Be(AtomicLegalExecutionTransitionOutcome.TargetChanged);

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(entry =>
            entry.LeaseAgreementId == rollback.LeaseAgreementId
            || entry.LeaseAgreementId == conflict.LeaseAgreementId))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task SignerSecurityUsesWallClockButBusinessFactsUseFrozenClock()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("future-token", 500m, signed: false);

        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.sign", "future-token"),
            new RecordNativeSignatureCommand(
                scenario.TokenHash,
                SignatureSignatureType.Typed,
                "Future Tenant",
                null,
                null,
                null,
                null,
                "127.0.0.1",
                "integration-test",
                DateTime.UtcNow),
            SignCodec);

        result.Value.Outcome.Should().Be(NativeSignerActionOutcome.Applied);
        await using var db = NewContext();
        var signer = await db.SignatureSigners.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.SignatureSignerId);
        signer.SignedAtUtc.Should().Be(FrozenBusinessNow);
        signer.TokenExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    private async Task<AtomicLegalExecutionTransitionResult> ExecuteTransitionAsync(Scenario scenario)
    {
        await using var db = NewContext();
        var originalAutoSavepointsEnabled = db.Database.AutoSavepointsEnabled;
        try
        {
            // This direct persistence harness mirrors AtomicTransactionRunner: journal-line
            // provenance is bound to the owner transaction XID, not an EF savepoint subtransaction.
            db.Database.AutoSavepointsEnabled = false;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var attemptId = Guid.NewGuid();
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.native-esign.transition", Guid.NewGuid().ToString("N")),
                attemptId,
                db);
            var result = await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
                db,
                commandContext,
                scenario.PortfolioId,
                scenario.LeaseManagementId,
                scenario.LeaseAgreementId,
                null,
                scenario.ExecutedArtifactId,
                FrozenBusinessNow);
            await transaction.CommitAsync();
            commandContext.EndAttempt();
            return result;
        }
        finally
        {
            db.Database.AutoSavepointsEnabled = originalAutoSavepointsEnabled;
        }
    }

    private async Task<Scenario> SeedScenarioAsync(
        string suffix,
        decimal depositObligation,
        bool completed = false,
        bool voided = false,
        bool signed = true)
    {
        await using var db = NewContext();
        var sequence = Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 10_000;
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Id = 50_000 + sequence,
            Name = $"E-sign Deposit {suffix}",
            ManagementCompanyName = "Rental Command",
            TimeZone = "America/New_York",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = ActorUserId + sequence,
            UserName = $"esign-deposit-{suffix}",
            NormalizedUserName = $"ESIGN-DEPOSIT-{suffix}".ToUpperInvariant(),
            Email = $"esign-deposit-{suffix}@example.test",
            NormalizedEmail = $"ESIGN-DEPOSIT-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            DisplayName = "E-sign Deposit User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var property = new Property
        {
            Id = 51_000 + sequence,
            Portfolio = portfolio,
            Name = $"Property {suffix}",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Id = 52_000 + sequence,
            PortfolioId = portfolio.Id,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            Id = 53_000 + sequence,
            Portfolio = portfolio,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = $"Lease {suffix}",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var source = new LegalDocumentSourceVersion
        {
            Id = 54_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{suffix}:v1",
            DocumentTemplate = template,
            DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var relationship = new LeaseManagement
        {
            Id = 55_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"REL-{suffix}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            Id = 56_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var agreementPublicId = Guid.NewGuid();
        var agreement = new LeaseAgreement
        {
            Id = 57_000 + sequence,
            PublicId = agreementPublicId,
            Portfolio = portfolio,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2027, 2, 1),
            TermEndOn = new DateOnly(2028, 1, 31),
            GoverningFromOn = new DateOnly(2027, 2, 1),
            BaseRentAmount = 1_000,
            RentDueDay = 1,
            SecurityDepositObligation = depositObligation,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var issuedStored = StoredFile(58_000 + sequence, portfolio, agreement.Id, $"issued-{suffix}.pdf", now);
        var issuedArtifact = Artifact(
            59_000 + sequence, portfolio, user.Id, issuedStored, LegalDocumentArtifactKind.IssuedAgreement, now);
        var executedStored = StoredFile(60_000 + sequence, portfolio, agreement.Id, $"executed-{suffix}.pdf", now);
        var executedArtifact = Artifact(
            61_000 + sequence, portfolio, user.Id, executedStored, LegalDocumentArtifactKind.ExecutedAgreement, now);
        var agreementSigner = new LeaseAgreementSigner
        {
            Id = 62_000 + sequence,
            Portfolio = portfolio,
            LeaseAgreement = agreement,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Future Tenant",
            EmailSnapshot = $"tenant-{suffix}@example.test",
            SigningOrder = 1,
            IsRequired = true,
        };
        var requestPublicId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(suffix)))
            .ToLowerInvariant();
        var request = new SignatureRequest
        {
            Id = 63_000 + sequence,
            Portfolio = portfolio,
            PublicId = requestPublicId,
            LeaseAgreement = agreement,
            Provider = "native",
            IdempotencyKey = $"request-{suffix}",
            Status = completed ? SignatureRequestStatus.Completed : SignatureRequestStatus.ExecutionPending,
            Subject = $"Agreement {suffix}",
            IssuedArtifact = issuedArtifact,
            IssuedArtifactId = issuedArtifact.Id,
            ExecutedArtifact = completed ? executedArtifact : null,
            ExecutedArtifactId = completed ? executedArtifact.Id : null,
            PreparedAtUtc = now,
            ProviderAcceptedAtUtc = now,
            CompletedAtUtc = completed ? now : null,
            CreatedByUserId = user.Id,
        };
        var signer = new SignatureSigner
        {
            Id = 64_000 + sequence,
            PortfolioId = portfolio.Id,
            SignatureRequest = request,
            AgreementSigner = agreementSigner,
            NameSnapshot = agreementSigner.NameSnapshot,
            EmailSnapshot = agreementSigner.EmailSnapshot,
            SigningOrder = 1,
            IsRequired = true,
            TokenHash = tokenHash,
            TokenExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            Status = signed ? SignatureSignerStatus.Signed : SignatureSignerStatus.Pending,
            SignatureType = signed ? SignatureSignatureType.Typed : SignatureSignatureType.None,
            TypedName = signed ? "Future Tenant" : null,
            ConsentGivenAtUtc = signed ? now : null,
            SignedAtUtc = signed ? now : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.AddRange(user, portfolio, property, unit, template, source, relationship, account,
            issuedStored, issuedArtifact, executedStored, executedArtifact, agreement,
            agreementSigner, request, signer);
        await db.SaveChangesAsync();

        await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        if (completed)
        {
            agreement.ExecutedArtifactId = executedArtifact.Id;
            agreement.FullyExecutedAtUtc = now;
        }
        if (voided)
        {
            agreement.VoidedAtUtc = now;
            agreement.VoidReasonCode = "test";
        }
        await db.SaveChangesAsync();

        return new Scenario(
            portfolio.Id,
            relationship.Id,
            account.Id,
            agreement.Id,
            agreementPublicId,
            request.Id,
            requestPublicId,
            signer.Id,
            tokenHash,
            executedArtifact.Id);
    }

    private static StoredFile StoredFile(
        int id,
        Portfolio portfolio,
        int agreementId,
        string fileName,
        DateTime now) => new()
    {
        Id = id,
        Portfolio = portfolio,
        FileName = fileName,
        FilePath = $"agreements/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1,
        EntityType = nameof(LeaseAgreement),
        EntityId = agreementId,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        int id,
        Portfolio portfolio,
        int userId,
        StoredFile storedFile,
        LegalDocumentArtifactKind kind,
        DateTime now) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Portfolio = portfolio,
        StoredFile = storedFile,
        ArtifactKind = kind,
        StorageKey = storedFile.FilePath,
        FileName = storedFile.FileName,
        ContentType = "application/pdf",
        ByteLength = 1,
        ContentSha256 = new string(kind == LegalDocumentArtifactKind.IssuedAgreement ? 'a' : 'b', 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement ? new string('c', 64) : null,
        CreatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static async Task FreezeClockAsync(RentalCommandDbContext db)
    {
        var clock = await db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = FrozenBusinessNow;
        clock.RealAnchorUtc = DateTime.UtcNow;
        clock.TimeZoneId = "America/New_York";
        await db.SaveChangesAsync();
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .AddInterceptors(_commandCounter)
            .Options);

    private IAtomicUnitOfWork Atomic =>
        _serviceScope!.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();

    private static async Task<int> CountDepositChargeOutboxMessagesAsync(
        RentalCommandDbContext db,
        IReadOnlyList<int> tenantAccountIds)
    {
        var rows = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM "OutboxMessages"
            WHERE "MessageType" = 'data-update'
              AND "Payload"::text LIKE '%"EntryType": "DepositCharge"%'
              AND EXISTS (
                  SELECT 1
                  FROM unnest({tenantAccountIds.ToArray()}) AS account("Id")
                  WHERE "Payload"::text LIKE
                      '%"TenantAccountId": ' || account."Id"::text || '%')
            """).ToListAsync();
        return rows.Single();
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int TenantAccountId,
        int LeaseAgreementId,
        Guid AgreementPublicId,
        int SignatureRequestId,
        Guid SignatureRequestPublicId,
        int SignatureSignerId,
        string TokenHash,
        int ExecutedArtifactId);

    private sealed class DepositBatchCommandCounter : DbCommandInterceptor
    {
        public int DepositBatchMutationCommandCount { get; private set; }

        public void Reset() => DepositBatchMutationCommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("FROM \"SignatureRequests\" AS request", StringComparison.Ordinal)
                && command.CommandText.Contains("INSERT INTO \"TenantLedgerEntries\"", StringComparison.Ordinal)
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal)
                && command.CommandText.Contains("request.\"Status\" = 'Completed'", StringComparison.Ordinal))
            {
                DepositBatchMutationCommandCount++;
            }
        }
    }
}
