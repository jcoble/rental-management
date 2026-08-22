using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Writes;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class ProviderPaymentWriteExecutorReceiptTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Fence = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClaimToken = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int PortfolioId = 91000;
    private const int UserId = 91001;
    private const int TenantId = 91002;
    private const int PartyId = 91003;
    private const int AccountId = 91004;
    private const long ChargeId = 91005;
    private const long AttemptId = 91006;
    private const long InboxId = 91007;

    // Frozen legacy fingerprints computed once from the command DTO shapes at c9dae23c.
    // These values must never be regenerated from the current command model or a codec helper.
    private const string PrepareFingerprint = "ffe3468788c204e4fc299dd99a3d7289af683fdbe448d3d57e795a25165e307a";
    private const string AutopayFingerprint = "250b58e65e01cdefc701fb777a2f077a46c0b9dd75a615eca96418ec6cc10c79";
    private const string SubmitFingerprint = "b08709453efbe8d87c5535358e53c8b5c85b3cc7246ea14939004760c3ca9e24";
    private const string ScheduleFingerprint = "a081fa7807f1889be13fb276f11e141f7e9e0e9e28eb586ac9f7a093f932777f";
    private const string FinalizeFingerprint = "db5af311402b4f587ef114ffd5266c2aa5fa3483235f638ab7c28f6a7e85f2f9";
    private const string FailFingerprint = "c4e6eef3e4e4313da121ad0373a025e13b028f83ef76ec1383f7553be0289658";
    private const string AbandonFingerprint = "d00b83c80e1d0d9270bfee0a08d3ccdeb08922e24a73c7bd85fbdeaa8edbb620";
    private const string InspectFingerprint = "8caaded90a3a2d35397dc41dc1d01ebbe598dc6269108d5f459022c097f71510";
    private const string EventFingerprint = "4f5f5949c17bac8026ab4275a9f29914eb801496eff47ae73047ffaf04df3dca";
    private const string ReconcileFingerprint = "a96c981beca907aa47557b985597461418d736a5abd8c470c13af1ea49eafa93";

    // Literal formulas hand-reproduced from the base callers at c9dae23c.
    private const string AttemptKey =
        "checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay";
    private const string AutopayKey = "autopay-setup:legacy-replay";
    private const string ScheduleKey = "91006:NETWORK:639229968000000000";
    private const string EventId = " evt_legacy_replay ";
    private const string EventKey = "stripe: evt_legacy_replay ";
    private const string ReconcileKey = "91007:22222222222222222222222222222222";

    [Fact]
    public void FrozenFingerprints_PreserveExactlyTheLegacyCommandFields()
    {
        var actual = new[]
        {
            AtomicCommandFingerprint.Create(Prepare()),
            AtomicCommandFingerprint.Create(Autopay()),
            AtomicCommandFingerprint.Create(Submit()),
            AtomicCommandFingerprint.Create(Schedule()),
            AtomicCommandFingerprint.Create(Finalize()),
            AtomicCommandFingerprint.Create(Fail()),
            AtomicCommandFingerprint.Create(Abandon()),
            AtomicCommandFingerprint.Create(Inspect()),
            AtomicCommandFingerprint.Create(Event()),
            AtomicCommandFingerprint.Create(Reconcile()),
        };
        actual.Should().Equal(PrepareFingerprint, AutopayFingerprint, SubmitFingerprint,
            ScheduleFingerprint, FinalizeFingerprint, FailFingerprint, AbandonFingerprint,
            InspectFingerprint, EventFingerprint, ReconcileFingerprint);

        Ignored(typeof(PrepareProviderPaymentCreateCommand)).Should().Equal("PreparedAtUtc");
        Ignored(typeof(PrepareProviderAutopaySetupCommand)).Should().Equal("PreparedAtUtc");
        Ignored(typeof(SubmitProviderPaymentCreateCommand)).Should().Equal("SubmittedAtUtc");
        Ignored(typeof(ScheduleProviderPaymentReconciliationCommand)).Should().Equal("ScheduledAtUtc");
        Ignored(typeof(FinalizeProviderPaymentCreateCommand)).Should().Equal("RecordedAtUtc");
        Ignored(typeof(FailProviderPaymentCreateCommand)).Should().Equal("RecordedAtUtc");
        Ignored(typeof(AbandonProviderPaymentAttemptCommand)).Should().Equal("AbandonedAtUtc");
        Ignored(typeof(InspectProviderPaymentAttemptCommand)).Should().BeEmpty();
        Ignored(typeof(RecordVerifiedProviderPaymentEventCommand)).Should().Equal("ReceivedAtUtc");
        Ignored(typeof(ReconcileClaimedProviderPaymentEventCommand)).Should().Equal("ReconciledAtUtc");
    }

    [Fact]
    public void Descriptors_PreserveAllTenOperationsContractsAndLockPrefixes()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().Options);
        Describe<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult>(
            db, "payments.provider-create.prepare", Prepare()).Should().BeEquivalentTo(
                new Descriptor("payments.provider-create.prepare",
                    "prepare-provider-payment-create-result.v1", WriteLockProtocol.TenantAccount,
                    ["TenantAccount"]));
        Describe<PrepareProviderAutopaySetupCommand, PrepareProviderAutopaySetupResult>(
            db, "payments.provider-autopay.prepare", Autopay()).Contract.Should().Be(
                "prepare-provider-autopay-setup-result.v1");
        Describe<SubmitProviderPaymentCreateCommand, SubmitProviderPaymentCreateResult>(
            db, "payments.provider-create.submit", Submit()).Contract.Should().Be(
                "submit-provider-payment-create-result.v1");
        Describe<ScheduleProviderPaymentReconciliationCommand, ScheduleProviderPaymentReconciliationResult>(
            db, "payments.provider-reconciliation.schedule", Schedule()).Contract.Should().Be(
                "schedule-provider-payment-reconciliation-result.v1");
        Describe<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>(
            db, "payments.provider-create.finalize:Succeeded", Finalize()).Contract.Should().Be(
                "finalize-provider-payment-create-result.v1");
        Describe<FailProviderPaymentCreateCommand, FailProviderPaymentCreateResult>(
            db, "payments.provider-create.fail", Fail()).Contract.Should().Be(
                "fail-provider-payment-create-result.v1");
        Describe<AbandonProviderPaymentAttemptCommand, AbandonProviderPaymentAttemptResult>(
            db, "payments.provider-attempt.abandon", Abandon()).Contract.Should().Be(
                "abandon-provider-payment-attempt-result.v1");
        Describe<InspectProviderPaymentAttemptCommand, InspectProviderPaymentAttemptResult>(
            db, "payments.provider-attempt.inspect", Inspect()).Contract.Should().Be(
                "inspect-provider-payment-attempt-result.v1");
        Describe<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult>(
            db, "payments.provider-event.record", Event()).Should().BeEquivalentTo(
                new Descriptor("payments.provider-event.record",
                    "record-verified-provider-payment-event-result.v1", null, []));
        Describe<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult>(
            db, "payments.provider-inbox.reconcile", Reconcile()).Should().BeEquivalentTo(
                new Descriptor("payments.provider-inbox.reconcile",
                    "reconcile-claimed-provider-payment-event-result.v1", null, []));
    }

    [Fact]
    public async Task AllTenLegacyHandlerArms_AreRetired()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().Options);
        Func<Task>[] calls =
        [
            () => new PrepareProviderPaymentCreateHandler(db).HandleAsync(Prepare(), null!, default),
            () => new PrepareProviderAutopaySetupHandler(db).HandleAsync(Autopay(), null!, default),
            () => new SubmitProviderPaymentCreateHandler(db).HandleAsync(Submit(), null!, default),
            () => new ScheduleProviderPaymentReconciliationHandler(db).HandleAsync(Schedule(), null!, default),
            () => new FinalizeProviderPaymentCreateHandler(db).HandleAsync(Finalize(), null!, default),
            () => new FailProviderPaymentCreateHandler(db).HandleAsync(Fail(), null!, default),
            () => new AbandonProviderPaymentAttemptHandler(db).HandleAsync(Abandon(), null!, default),
            () => new InspectProviderPaymentAttemptHandler(db).HandleAsync(Inspect(), null!, default),
            () => new RecordVerifiedProviderPaymentEventHandler(db).HandleAsync(Event(), null!, default),
            () => new ReconcileClaimedProviderPaymentEventHandler(db).HandleAsync(Reconcile(), null!, default),
        ];

        foreach (var call in calls)
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Legacy atomic provider-payment writes are retired; use the shared write executor.");
    }

    [Fact]
    public void FrozenStoredResults_DecodeAllTenLegacyContractsAndJsonShapes()
    {
        AssertFrozen<PrepareProviderPaymentCreateResult>(
            "prepare-provider-payment-create-result.v1", PrepareJson,
            "Outcome", "PortfolioId", "TenantAccountId", "ChargeLedgerEntryId", "PaymentAttemptId",
            "Amount", "Currency", "Provider", "IdempotencyKey", "ProviderCustomerId",
            "ProviderPaymentMethodId", "State", "ProviderFenceToken", "ProviderPaymentId", "PreparedAtUtc");
        AssertFrozen<PrepareProviderAutopaySetupResult>(
            "prepare-provider-autopay-setup-result.v1", AutopayJson,
            "Outcome", "PortfolioId", "TenantAccountId", "AuthorizingPartyId", "ActorUserId",
            "PaymentAttemptId", "Provider", "IdempotencyKey", "State", "ProviderFenceToken",
            "ProviderPaymentId");
        AssertFrozen<SubmitProviderPaymentCreateResult>(
            "submit-provider-payment-create-result.v1", SubmitJson,
            "Outcome", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "State", "Amount",
            "Currency", "Provider", "IdempotencyKey", "ProviderFenceToken", "ProviderPaymentId",
            "PreparedAtUtc");
        AssertFrozen<ScheduleProviderPaymentReconciliationResult>(
            "schedule-provider-payment-reconciliation-result.v1", ScheduleJson,
            "Outcome", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "State", "NextAttemptAtUtc");
        AssertFrozen<FinalizeProviderPaymentCreateResult>(
            "finalize-provider-payment-create-result.v1", FinalizeJson,
            "Outcome", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "Provider",
            "ProviderPaymentId", "State");
        AssertFrozen<FailProviderPaymentCreateResult>(
            "fail-provider-payment-create-result.v1", FailJson,
            "Found", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "State");
        AssertFrozen<AbandonProviderPaymentAttemptResult>(
            "abandon-provider-payment-attempt-result.v1", AbandonJson,
            "Outcome", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "State");
        AssertFrozen<InspectProviderPaymentAttemptResult>(
            "inspect-provider-payment-attempt-result.v1", InspectJson,
            "Found", "PortfolioId", "TenantAccountId", "PaymentAttemptId", "AttemptType", "State",
            "Amount", "Currency", "Provider", "IdempotencyKey", "ProviderPaymentId",
            "ProviderFenceToken", "PreparedAtUtc");
        AssertFrozen<RecordVerifiedProviderPaymentEventResult>(
            "record-verified-provider-payment-event-result.v1", EventJson,
            "Outcome", "ProviderInboxEventId", "PortfolioId", "TenantAccountId", "PaymentAttemptId",
            "AttemptState");
        AssertFrozen<ReconcileClaimedProviderPaymentEventResult>(
            "reconcile-claimed-provider-payment-event-result.v1", ReconcileJson,
            "Outcome", "ProviderInboxEventId", "PortfolioId", "TenantAccountId", "PaymentAttemptId",
            "AttemptState", "NextAttemptAtUtc");
    }

    [Fact]
    public async Task FrozenLegacyReceipts_ReplayAllTenContracts_AndRecheckAuthorization()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedAuthorityAndReceiptsAsync(database.Db);
        await using var services = Services(database.ConnectionString);

        await ReplayRequestAsync<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult>(
            services, AttemptKey, "payments.provider-create.prepare", Prepare(), PrepareJson);
        await ReplayRequestAsync<PrepareProviderAutopaySetupCommand, PrepareProviderAutopaySetupResult>(
            services, AutopayKey, "payments.provider-autopay.prepare", Autopay(), AutopayJson);
        await ReplayRequestAsync<SubmitProviderPaymentCreateCommand, SubmitProviderPaymentCreateResult>(
            services, AttemptKey, "payments.provider-create.submit", Submit(), SubmitJson);
        await ReplayJobAsync<ScheduleProviderPaymentReconciliationCommand,
            ScheduleProviderPaymentReconciliationResult>(
            services, ScheduleKey, "payments.provider-reconciliation.schedule", Schedule(), ScheduleJson);
        await ReplayRequestAsync<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>(
            services, AttemptKey, "payments.provider-create.finalize:Succeeded", Finalize(), FinalizeJson);
        await ReplayRequestAsync<FailProviderPaymentCreateCommand, FailProviderPaymentCreateResult>(
            services, AttemptKey, "payments.provider-create.fail", Fail(), FailJson);
        await ReplayRequestAsync<AbandonProviderPaymentAttemptCommand, AbandonProviderPaymentAttemptResult>(
            services, AttemptKey, "payments.provider-attempt.abandon", Abandon(), AbandonJson);
        await ReplayRequestAsync<InspectProviderPaymentAttemptCommand, InspectProviderPaymentAttemptResult>(
            services, AttemptId.ToString(), "payments.provider-attempt.inspect", Inspect(), InspectJson);
        await ReplayExactRequestAsync<RecordVerifiedProviderPaymentEventCommand,
            RecordVerifiedProviderPaymentEventResult>(
            services, EventKey, "payments.provider-event.record", Event(), EventJson);
        await ReplayJobAsync<ReconcileClaimedProviderPaymentEventCommand,
            ReconcileClaimedProviderPaymentEventResult>(
            services, ReconcileKey, "payments.provider-inbox.reconcile", Reconcile(), ReconcileJson);

        await using (var revoke = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(database.ConnectionString).Options))
        {
            await revoke.TenantUserAccesses.Where(access => access.ApplicationUserId == UserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(access => access.RevokedAtUtc, Now)
                    .SetProperty(access => access.RevokedByUserId, UserId));
        }
        var denied = () => ReplayRequestAsync<PrepareProviderAutopaySetupCommand,
            PrepareProviderAutopaySetupResult>(
            services, AutopayKey, "payments.provider-autopay.prepare", Autopay(), AutopayJson);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static async Task ReplayRequestAsync<TCommand, TResult>(
        ServiceProvider services, string key, string operation, TCommand command, string storedJson)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = ProviderPaymentWriteSupport.Write<TCommand, TResult>(db, operation, command);
        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        write.ResultContract.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task ReplayExactRequestAsync<TCommand, TResult>(
        ServiceProvider services, string key, string operation, TCommand command, string storedJson)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = ProviderPaymentWriteSupport.Write<TCommand, TResult>(db, operation, command);
        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteExactAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
    }

    private static async Task ReplayJobAsync<TCommand, TResult>(
        ServiceProvider services, string key, string operation, TCommand command, string storedJson)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = ProviderPaymentWriteSupport.Write<TCommand, TResult>(db, operation, command);
        var outcome = await scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>()
            .ExecuteAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
    }

    private static async Task SeedAuthorityAndReceiptsAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = UserId, UserName = "provider-replay@example.test",
            NormalizedUserName = "PROVIDER-REPLAY@EXAMPLE.TEST", Email = "provider-replay@example.test",
            NormalizedEmail = "PROVIDER-REPLAY@EXAMPLE.TEST", DisplayName = "Provider Replay",
            SecurityStamp = "provider-replay", ConcurrencyStamp = "provider-replay", CreatedAt = Now,
        };
        var portfolio = new Portfolio
        {
            Id = PortfolioId, Name = "Provider Replay", ManagementCompanyName = "Provider Replay",
            TimeZone = "UTC", Currency = "USD", CreatedAt = Now, UpdatedAt = Now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        var context = new WorkspaceAccessContext
        {
            Id = 91008, UserId = UserId, PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var property = new Property
        {
            Id = 91009, PortfolioId = PortfolioId, Name = "Replay Property", AddressLine1 = "1 Replay Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = Now, UpdatedAt = Now,
        };
        var unit = new Unit
        {
            Id = 91010, PortfolioId = PortfolioId, Property = property, UnitNumber = "1",
            CreatedAt = Now, UpdatedAt = Now,
        };
        var tenant = new Tenant
        {
            Id = TenantId, PortfolioId = PortfolioId, FirstName = "Legacy", LastName = "Replay",
            CreatedAt = Now, UpdatedAt = Now,
        };
        db.AddRange(context, property, unit, tenant);
        await db.SaveChangesAsync();
        var relationship = new LeaseManagement
        {
            Id = 91011, PortfolioId = PortfolioId, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = "LM-LEGACY-REPLAY", CreatedAtUtc = Now, UpdatedAtUtc = Now,
            CreatedByUserId = UserId,
        };
        db.Add(relationship);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            Id = AccountId, PortfolioId = PortfolioId, LeaseManagementId = relationship.Id,
            AccountNumber = "TA-LEGACY-REPLAY", Currency = "USD", OpenedAtUtc = Now,
            CreatedAtUtc = Now, CreatedByUserId = UserId,
        };
        var party = new LeaseManagementParty
        {
            Id = PartyId, PortfolioId = PortfolioId, LeaseManagementId = relationship.Id,
            TenantId = TenantId, Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 1, 1), ChangeReason = "legacy replay",
            CreatedAtUtc = Now, CreatedByUserId = UserId,
        };
        db.AddRange(account, party);
        await db.SaveChangesAsync();
        db.Add(new TenantUserAccess
        {
            Id = 91012, PublicId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            PortfolioId = PortfolioId, AccessContextId = context.Id, ApplicationUserId = UserId,
            LeaseManagementPartyId = PartyId, GrantedAtUtc = Now, GrantedByUserId = UserId,
            Reason = "legacy replay",
        });
        db.Add(new TenantLedgerEntry
        {
            Id = ChargeId, PortfolioId = PortfolioId, TenantAccountId = AccountId,
            EntryType = TenantLedgerEntryType.ManualCharge, Direction = TenantLedgerDirection.Debit,
            Amount = 100m, Currency = "USD", EffectiveOn = new DateOnly(2026, 8, 21),
            DueOn = new DateOnly(2026, 8, 21), PostedAtUtc = Now,
            Description = "Legacy replay charge", BusinessKey = "rent:legacy-replay",
            CreatedByUserId = UserId,
        });
        db.Add(new TenantPaymentAttempt
        {
            Id = AttemptId, PortfolioId = PortfolioId, TenantAccountId = AccountId,
            ChargeLedgerEntryId = ChargeId, Provider = "stripe", ProviderObjectId = "pi_legacy_replay",
            ProviderFenceToken = Fence, IdempotencyKey = AttemptKey,
            AttemptType = TenantPaymentAttemptType.Charge, State = TenantPaymentAttemptState.Submitted,
            Amount = 100m, Currency = "USD", PreparedAtUtc = Now, SubmittedAtUtc = Now,
            UpdatedAtUtc = Now, CreatedByUserId = UserId,
        });
        db.Add(new ProviderInboxEvent
        {
            Id = InboxId, Provider = "stripe", ProviderEventId = EventId,
            EventType = "payment_intent.succeeded", Payload = "{}", ProviderObjectId = "pi_legacy_replay",
            EventKind = ProviderPaymentEventKind.Succeeded, Amount = 100m, Currency = "USD",
            OccurredAtUtc = Now, ReceivedAtUtc = Now, NextAttemptAtUtc = Now,
            ProcessedAtUtc = Now, PortfolioId = PortfolioId, AttemptCount = 1,
        });
        await db.SaveChangesAsync();

        AddReceipt(db, "payments.provider-create.prepare", AttemptKey, PrepareFingerprint,
            "prepare-provider-payment-create-result.v1", PrepareJson);
        AddReceipt(db, "payments.provider-autopay.prepare", AutopayKey, AutopayFingerprint,
            "prepare-provider-autopay-setup-result.v1", AutopayJson);
        AddReceipt(db, "payments.provider-create.submit", AttemptKey, SubmitFingerprint,
            "submit-provider-payment-create-result.v1", SubmitJson);
        AddReceipt(db, "payments.provider-reconciliation.schedule", ScheduleKey, ScheduleFingerprint,
            "schedule-provider-payment-reconciliation-result.v1", ScheduleJson);
        AddReceipt(db, "payments.provider-create.finalize:Succeeded", AttemptKey, FinalizeFingerprint,
            "finalize-provider-payment-create-result.v1", FinalizeJson);
        AddReceipt(db, "payments.provider-create.fail", AttemptKey, FailFingerprint,
            "fail-provider-payment-create-result.v1", FailJson);
        AddReceipt(db, "payments.provider-attempt.abandon", AttemptKey, AbandonFingerprint,
            "abandon-provider-payment-attempt-result.v1", AbandonJson);
        AddReceipt(db, "payments.provider-attempt.inspect", AttemptId.ToString(), InspectFingerprint,
            "inspect-provider-payment-attempt-result.v1", InspectJson);
        AddReceipt(db, "payments.provider-event.record", EventKey, EventFingerprint,
            "record-verified-provider-payment-event-result.v1", EventJson);
        AddReceipt(db, "payments.provider-inbox.reconcile", ReconcileKey, ReconcileFingerprint,
            "reconcile-claimed-provider-payment-event-result.v1", ReconcileJson);
        await db.SaveChangesAsync();
    }

    private static void AddReceipt(RentalCommandDbContext db, string operation, string key,
        string fingerprint, string contract, string json) => db.AtomicCommandReceipts.Add(new()
    {
        Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = operation,
        IdempotencyKey = key, RequestFingerprint = fingerprint,
        Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract, ResultJson = json,
        StartedAt = Now, CompletedAt = Now,
    });

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private static Descriptor Describe<TCommand, TResult>(
        RentalCommandDbContext db, string operation, TCommand command)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        var write = ProviderPaymentWriteSupport.Write<TCommand, TResult>(db, operation, command);
        return new(write.OperationName, write.ResultContract, write.LockPlan.Protocol,
            write.LockPlan.Locks.Select(item => item.LockNamespace).ToArray());
    }

    private static void AssertFrozen<TResult>(string contract, string storedJson, params string[] properties)
        where TResult : notnull
    {
        var codec = new AtomicJsonResultCodec<TResult>(contract);
        var decoded = codec.Deserialize(storedJson);
        using var stored = JsonDocument.Parse(storedJson);
        using var replayed = JsonDocument.Parse(codec.Serialize(decoded));
        stored.RootElement.EnumerateObject().Select(item => item.Name).Should().Equal(properties);
        JsonElement.DeepEquals(stored.RootElement, replayed.RootElement).Should().BeTrue();
    }

    private static string[] Ignored(Type type) => type.GetProperties()
        .Where(property => property.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null)
        .Select(property => property.Name).ToArray();

    private static PrepareProviderPaymentCreateCommand Prepare() => new(
        PortfolioId, AccountId, ChargeId, UserId, TenantId, null, "stripe", AttemptKey, "USD", Now);
    private static PrepareProviderAutopaySetupCommand Autopay() => new(
        PortfolioId, AccountId, TenantId, UserId, "stripe", AutopayKey, "USD", Now);
    private static SubmitProviderPaymentCreateCommand Submit() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, Now);
    private static ScheduleProviderPaymentReconciliationCommand Schedule() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, Fence,
        new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc), "NETWORK", "retry", Now);
    private static FinalizeProviderPaymentCreateCommand Finalize() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "pi_legacy_replay",
        TenantPaymentAttemptState.Succeeded, null, Now, Fence);
    private static FailProviderPaymentCreateCommand Fail() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "DECLINED", "declined", Now, Fence);
    private static AbandonProviderPaymentAttemptCommand Abandon() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "abandoned", Now,
        true, TenantPaymentAttemptState.Canceled, "pi_legacy_replay");
    private static InspectProviderPaymentAttemptCommand Inspect() => new(
        PortfolioId, TenantId, AccountId, AttemptId, "stripe");
    private static RecordVerifiedProviderPaymentEventCommand Event() => new(
        "stripe", EventId, "payment_intent.succeeded", "{}", "pi_legacy_replay",
        ProviderPaymentEventKind.Succeeded, 100m, "USD", null, Now, Now);
    private static ReconcileClaimedProviderPaymentEventCommand Reconcile() => new(
        InboxId, "worker-legacy", ClaimToken, Now);

    private const string PrepareJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"ChargeLedgerEntryId\":91005,\"PaymentAttemptId\":91006,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderCustomerId\":null,\"ProviderPaymentMethodId\":null,\"State\":1,\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string AutopayJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"AuthorizingPartyId\":91003,\"ActorUserId\":91001,\"PaymentAttemptId\":91006,\"Provider\":\"stripe\",\"IdempotencyKey\":\"autopay-setup:legacy-replay\",\"State\":1,\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"pi_legacy_replay\"}";
    private const string SubmitJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":1,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string ScheduleJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":1,\"NextAttemptAtUtc\":\"2026-08-22T12:00:00Z\"}";
    private const string FinalizeJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"Provider\":\"stripe\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"State\":2}";
    private const string FailJson =
        "{\"Found\":true,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":3}";
    private const string AbandonJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":4}";
    private const string InspectJson =
        "{\"Found\":true,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptType\":0,\"State\":1,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string EventJson =
        "{\"Outcome\":0,\"ProviderInboxEventId\":91007,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptState\":2}";
    private const string ReconcileJson =
        "{\"Outcome\":0,\"ProviderInboxEventId\":91007,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptState\":2,\"NextAttemptAtUtc\":null}";

    private sealed record Descriptor(
        string Operation, string Contract, WriteLockProtocol? Protocol, string[] Locks);
}
