using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection3.Name)]
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
    private const long AutopayAttemptId = 91014;
    private const long EnsureExpireAttemptId = 91015;
    private const long ScheduleAttemptId = 91016;

    // Frozen legacy fingerprints computed once from the command DTO shapes at c9dae23c.
    // These values must never be regenerated from the current command model or a codec helper.
    private const string PrepareFingerprint = "ffe3468788c204e4fc299dd99a3d7289af683fdbe448d3d57e795a25165e307a";
    private const string AutopayFingerprint = "250b58e65e01cdefc701fb777a2f077a46c0b9dd75a615eca96418ec6cc10c79";
    private const string SubmitFingerprint = "b08709453efbe8d87c5535358e53c8b5c85b3cc7246ea14939004760c3ca9e24";
    private const string EnsureFenceFingerprint = "d542a79348a086ce725882b95b82735aa4a51a9e0a70b42be886fbe8c3b4577a";
    private const string ScheduleFingerprint = "369b96146f5cbb2806e214f315697a3355a3b5f34d87ecfe83efe463fe7f022a";
    private const string FinalizeFingerprint = "db5af311402b4f587ef114ffd5266c2aa5fa3483235f638ab7c28f6a7e85f2f9";
    private const string FailFingerprint = "402c2d67fe9dea7a22157173f072fdba0a0c21e92f753ff4ba2faf884cb58bf8";
    private const string AbandonFingerprint = "d00b83c80e1d0d9270bfee0a08d3ccdeb08922e24a73c7bd85fbdeaa8edbb620";
    private const string InspectFingerprint = "8caaded90a3a2d35397dc41dc1d01ebbe598dc6269108d5f459022c097f71510";
    private const string EventFingerprint = "898327e8cb09f4ce2add42087bffa4d5d84f7024c635ca14b64475454cfa70e2";
    private const string ReconcileFingerprint = "a96c981beca907aa47557b985597461418d736a5abd8c470c13af1ea49eafa93";
    private const string AutopaySubmitFingerprint = "bc09512e87e9733c640cabc1e1b9dbf720d8401e608dbf40f6d0458835f217c2";
    private const string ExpireFingerprint = "8d0ab018b7833148e67fe3ab512940e9266d27d8006fa90cb9b35a903be31a7a";

    // Literal formulas hand-reproduced from the base callers at c9dae23c.
    private const string AttemptKey =
        "checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay";
    private const string AutopayKey = "autopay-setup:legacy-replay";
    private const string EnsureExpireAttemptKey = "verification:legacy-ensure-expire";
    private const string ScheduleAttemptKey = "verification:legacy-schedule";
    private const string ScheduleKey =
        "91016:PROVIDER_RECONCILE_UNKNOWN:639229968000000000";
    private const string EventId = " evt_legacy_replay ";
    private const string EventKey = "stripe: evt_legacy_replay ";
    private const string ExpireKey =
        "91015:verification:legacy-ensure-expire";
    private const string ReconcileKey = "91007:22222222222222222222222222222222";
    private const string WebhookSecret = "whsec_legacy_replay";

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
            AtomicCommandFingerprint.Create(AutopaySubmit()),
            AtomicCommandFingerprint.Create(EnsureFence()),
            AtomicCommandFingerprint.Create(Expire()),
        };
        actual.Should().Equal(PrepareFingerprint, AutopayFingerprint, SubmitFingerprint,
            ScheduleFingerprint, FinalizeFingerprint, FailFingerprint, AbandonFingerprint,
            InspectFingerprint, EventFingerprint, ReconcileFingerprint, AutopaySubmitFingerprint,
            EnsureFenceFingerprint, ExpireFingerprint);

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
    public async Task FrozenLegacyReceipts_ReplayThroughProductionCallers_AndRecheckAuthorization()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedAuthorityAndReceiptsAsync(database.Db);
        var interactiveProvider = new ReceiptInteractiveProviderClient();
        var autopayProvider = new ReceiptAutopayProviderClient();
        var inboxClaims = new FrozenProviderInboxClaimStore();
        await using var services = Services(
            database.ConnectionString, interactiveProvider, autopayProvider, inboxClaims);

        interactiveProvider.Reconciled = ConfirmedNoProviderObject(AttemptKey);
        var failed = await CreateCheckoutAsync(services, Now.AddHours(25));
        failed.Result.Should().Be(CheckoutResult.Outcome.AttemptFailed);

        interactiveProvider.Reconciled = ProviderObject("succeeded", AttemptKey);
        var succeeded = await CreateCheckoutAsync(services, Now.AddHours(25));
        succeeded.Result.Should().Be(CheckoutResult.Outcome.AlreadyPaid);
        succeeded.ProviderPaymentId.Should().Be("pi_legacy_replay");

        interactiveProvider.Reconciled = null;
        var autopaySetup = await CreateAutopaySetupAsync(services, Now);
        autopaySetup.Result.Should().Be(CheckoutResult.Outcome.AttemptPending);

        interactiveProvider.Reconciled = ProviderObject("canceled", AttemptKey);
        var canceled = await CancelCheckoutAsync(services, Now);
        canceled.Result.Should().Be(CheckoutResult.Outcome.AttemptCanceled);

        await ReplayWebhookAsync(services, Now);

        interactiveProvider.SetReconciled(
            EnsureExpireAttemptId, ConfirmedNoProviderObject(EnsureExpireAttemptKey));
        interactiveProvider.SetReconciled(ScheduleAttemptId, null);
        (await RunInteractiveReconciliationAsync(services, Now)).Should().Be(2);

        (await RunAutopayAsync(services, Now)).Should().Be(0);
        (await RunProviderInboxAsync(services, Now)).Should().Be(1);

        interactiveProvider.CheckoutCreateCount.Should().Be(0);
        interactiveProvider.SetupCreateCount.Should().Be(0);
        interactiveProvider.PaymentIntentCreateCount.Should().Be(0);
        autopayProvider.CreateCount.Should().Be(0);
        autopayProvider.ReconcileCount.Should().Be(1);
        inboxClaims.ClaimCount.Should().Be(1);

        await using (var verify = NewContext(database.ConnectionString))
        {
            (await verify.TenantPaymentAttempts.CountAsync(row =>
                row.Id == AttemptId || row.Id == AutopayAttemptId
                || row.Id == EnsureExpireAttemptId || row.Id == ScheduleAttemptId)).Should().Be(4);
            (await verify.TenantPaymentAttempts.CountAsync()).Should().Be(4);
            (await verify.ProviderInboxEvents.CountAsync(row => row.Id == InboxId)).Should().Be(1);
            var inbox = await verify.ProviderInboxEvents.SingleAsync(row => row.Id == InboxId);
            inbox.AttemptCount.Should().Be(1);
            inbox.ProcessedAtUtc.Should().Be(Now);
            (await verify.AtomicCommandReceipts.CountAsync()).Should().Be(14);
        }

        await using (var revoke = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(database.ConnectionString).Options))
        {
            await revoke.TenantUserAccesses.Where(access => access.ApplicationUserId == UserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(access => access.RevokedAtUtc, Now)
                    .SetProperty(access => access.RevokedByUserId, UserId));
        }
        var denied = () => CreateAutopaySetupAsync(services, Now);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static async Task<CheckoutResult> CreateCheckoutAsync(
        ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        return await StripeService(scope.ServiceProvider, now).CreatePaymentCheckoutSessionAsync(
            PortfolioId, TenantId, AccountId, ChargeId, UserId, null, null, default,
            " legacy-replay ");
    }

    private static async Task<CheckoutResult> CreateAutopaySetupAsync(
        ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        return await StripeService(scope.ServiceProvider, now).CreateAutopaySetupSessionAsync(
            PortfolioId, TenantId, AccountId, UserId, " legacy-replay ", null, null, default);
    }

    private static async Task<CheckoutResult> CancelCheckoutAsync(
        ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        return await StripeService(scope.ServiceProvider, now).CancelPaymentAttemptAsync(
            PortfolioId, TenantId, AccountId, AttemptId, "abandoned", default);
    }

    private static async Task ReplayWebhookAsync(ServiceProvider services, DateTime now)
    {
        const string json =
            "{\"id\":\" evt_legacy_replay \",\"object\":\"event\",\"type\":\"legacy.replay\",\"request\":null,\"data\":{\"object\":{\"id\":\"obj_legacy\",\"object\":\"payment_intent\"}}}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{json}")))
            .ToLowerInvariant();
        await using var scope = services.CreateAsyncScope();
        await StripeService(scope.ServiceProvider, now).HandleWebhookEventAsync(
            json, $"t={timestamp},v1={signature}", default);
    }

    private static async Task<int> RunInteractiveReconciliationAsync(
        ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        return await new InteractivePaymentReconciliationService(
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IJobStepWriteExecutor>(),
            provider.GetRequiredService<IInteractivePaymentProviderClient>(),
            new FixedTimeProvider(now),
            Options.Create(new InteractivePaymentReconciliationOptions
            {
                BatchSize = 2,
                RetryDelay = TimeSpan.FromDays(1),
                Expiration = TimeSpan.FromDays(1),
            }),
            NullLogger<InteractivePaymentReconciliationService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>()).ReconcileAsync();
    }

    private static async Task<int> RunAutopayAsync(ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        return await new AutopayChargeService(
            provider.GetRequiredService<RentalCommandDbContext>(),
            Options.Create(new StripeConfig { SecretKey = "sk_test_legacy_replay" }),
            new FixedTimeProvider(now),
            provider.GetRequiredService<IJobStepWriteExecutor>(),
            NullLogger<AutopayChargeService>.Instance,
            provider.GetRequiredService<IAutopayProviderClient>()).ChargeDueAsync();
    }

    private static async Task<int> RunProviderInboxAsync(ServiceProvider services, DateTime now)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        return await new ProviderInboxReconciliationService(
            provider.GetRequiredService<IProviderInboxClaimStore>(),
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IJobStepWriteExecutor>(),
            new FixedTimeProvider(now),
            NullLogger<ProviderInboxReconciliationService>.Instance).ReconcileAsync();
    }

    private static StripePaymentService StripeService(IServiceProvider provider, DateTime now) => new(
        Options.Create(new StripeConfig
        {
            SecretKey = "sk_test_legacy_replay",
            PublishableKey = "pk_test_legacy_replay",
            WebhookSecret = WebhookSecret,
        }),
        provider.GetRequiredService<ISandboxGuard>(),
        NullLogger<StripePaymentService>.Instance,
        new FixedTimeProvider(now),
        provider.GetRequiredService<RentalCommandDbContext>(),
        provider.GetRequiredService<IRequestWriteExecutor>(),
        provider.GetRequiredService<IInteractivePaymentProviderClient>());

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
            NextAttemptAtUtc = Now.AddYears(1), UpdatedAtUtc = Now, CreatedByUserId = UserId,
        });
        db.Add(new TenantPaymentAttempt
        {
            Id = AutopayAttemptId, PortfolioId = PortfolioId, TenantAccountId = AccountId,
            ProviderObjectId = "seti_legacy_replay", ProviderFenceToken = Fence,
            Provider = "stripe", IdempotencyKey = AutopayKey,
            AttemptType = TenantPaymentAttemptType.Verification,
            State = TenantPaymentAttemptState.Submitted,
            Amount = 0m, Currency = "USD", PreparedAtUtc = Now, SubmittedAtUtc = Now,
            NextAttemptAtUtc = Now.AddYears(1), UpdatedAtUtc = Now, CreatedByUserId = UserId,
        });
        db.AddRange(
            new TenantPaymentAttempt
            {
                Id = EnsureExpireAttemptId, PortfolioId = PortfolioId, TenantAccountId = AccountId,
                Provider = "stripe", IdempotencyKey = EnsureExpireAttemptKey,
                AttemptType = TenantPaymentAttemptType.Verification,
                State = TenantPaymentAttemptState.Submitted,
                Amount = 0m, Currency = "USD", PreparedAtUtc = Now.AddDays(-2),
                SubmittedAtUtc = Now.AddDays(-2), UpdatedAtUtc = Now, CreatedByUserId = UserId,
            },
            new TenantPaymentAttempt
            {
                Id = ScheduleAttemptId, PortfolioId = PortfolioId, TenantAccountId = AccountId,
                Provider = "stripe", IdempotencyKey = ScheduleAttemptKey,
                ProviderFenceToken = Fence, AttemptType = TenantPaymentAttemptType.Verification,
                State = TenantPaymentAttemptState.Submitted,
                Amount = 0m, Currency = "USD", PreparedAtUtc = Now,
                SubmittedAtUtc = Now, UpdatedAtUtc = Now, CreatedByUserId = UserId,
            });
        db.Add(new TenantAutopayEnrollment
        {
            Id = 91013, PortfolioId = PortfolioId, TenantAccountId = AccountId,
            AuthorizingPartyId = PartyId, Provider = "stripe",
            ProviderCustomerId = "cus_legacy_replay", ProviderPaymentMethodId = "pm_legacy_replay",
            EnrolledAtUtc = Now, CreatedByUserId = UserId,
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
        AddReceipt(db, "payments.provider-create.submit", AutopayKey, AutopaySubmitFingerprint,
            "submit-provider-payment-create-result.v1", AutopaySubmitJson);
        // Frozen caller identities calculated once by hand from the legacy base formulas.
        // Never regenerate these fixtures from current helpers or command metadata.
        AddReceipt(db, "payments.provider-reconciliation.ensure-fence", EnsureExpireAttemptKey,
            EnsureFenceFingerprint, "submit-provider-payment-create-result.v1", EnsureFenceJson);
        AddReceipt(db, "payments.provider-create.finalize", AttemptKey, FinalizeFingerprint,
            "finalize-provider-payment-create-result.v1", FinalizeJson);
        AddReceipt(db, "payments.provider-reconciliation.expire", ExpireKey, ExpireFingerprint,
            "fail-provider-payment-create-result.v1", ExpireJson);
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

    private static ServiceProvider Services(
        string connectionString,
        ReceiptInteractiveProviderClient interactiveProvider,
        ReceiptAutopayProviderClient autopayProvider,
        FrozenProviderInboxClaimStore inboxClaims)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddSingleton<ISandboxGuard, NeverSandboxGuard>();
        services.AddSingleton<IInteractivePaymentProviderClient>(interactiveProvider);
        services.AddSingleton<IAutopayProviderClient>(autopayProvider);
        services.AddSingleton<IProviderInboxClaimStore>(inboxClaims);
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
        PortfolioId, AccountId, ScheduleAttemptId, "stripe", ScheduleAttemptKey, Fence,
        new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc),
        "PROVIDER_RECONCILE_UNKNOWN",
        "Provider reconciliation returned no definitive result.", Now);
    private static FinalizeProviderPaymentCreateCommand Finalize() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "pi_legacy_replay",
        TenantPaymentAttemptState.Succeeded, null, Now, Fence);
    private static FailProviderPaymentCreateCommand Fail() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "PROVIDER_RECONCILE_EXPIRED",
        "Provider reconciliation found no accepted payment after 24 hours.", Now, Fence);
    private static AbandonProviderPaymentAttemptCommand Abandon() => new(
        PortfolioId, AccountId, AttemptId, "stripe", AttemptKey, "abandoned", Now,
        true, TenantPaymentAttemptState.Canceled, "pi_legacy_replay");
    private static InspectProviderPaymentAttemptCommand Inspect() => new(
        PortfolioId, TenantId, AccountId, AttemptId, "stripe");
    private static RecordVerifiedProviderPaymentEventCommand Event() => new(
        "stripe", EventId, "legacy.replay",
        "{\"Id\":\" evt_legacy_replay \",\"Type\":\"legacy.replay\"}",
        "event: evt_legacy_replay ", ProviderPaymentEventKind.Ignored,
        null, null, null, Now, Now);
    private static ReconcileClaimedProviderPaymentEventCommand Reconcile() => new(
        InboxId, "worker-legacy", ClaimToken, Now);
    private static SubmitProviderPaymentCreateCommand AutopaySubmit() => new(
        PortfolioId, AccountId, AutopayAttemptId, "stripe", AutopayKey, Now);
    private static SubmitProviderPaymentCreateCommand EnsureFence() => new(
        PortfolioId, AccountId, EnsureExpireAttemptId, "stripe", EnsureExpireAttemptKey, Now);
    private static FailProviderPaymentCreateCommand Expire() => new(
        PortfolioId, AccountId, EnsureExpireAttemptId, "stripe", EnsureExpireAttemptKey,
        "PROVIDER_RECONCILE_EXPIRED",
        "Provider reconciliation found no accepted payment after the expiry policy.", Now, Fence);

    private const string PrepareJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"ChargeLedgerEntryId\":91005,\"PaymentAttemptId\":91006,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderCustomerId\":null,\"ProviderPaymentMethodId\":null,\"State\":1,\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string AutopayJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"AuthorizingPartyId\":91003,\"ActorUserId\":91001,\"PaymentAttemptId\":91014,\"Provider\":\"stripe\",\"IdempotencyKey\":\"autopay-setup:legacy-replay\",\"State\":1,\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"seti_legacy_replay\"}";
    private const string SubmitJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":1,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string AutopaySubmitJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91014,\"State\":1,\"Amount\":0,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"autopay-setup:legacy-replay\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":\"seti_legacy_replay\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string EnsureFenceJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91015,\"State\":1,\"Amount\":0,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"verification:legacy-ensure-expire\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"ProviderPaymentId\":null,\"PreparedAtUtc\":\"2026-08-19T12:00:00Z\"}";
    private const string ScheduleJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91016,\"State\":1,\"NextAttemptAtUtc\":\"2026-08-22T12:00:00Z\"}";
    private const string FinalizeJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"Provider\":\"stripe\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"State\":2}";
    private const string FailJson =
        "{\"Found\":true,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":3}";
    private const string ExpireJson =
        "{\"Found\":true,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91015,\"State\":3}";
    private const string AbandonJson =
        "{\"Outcome\":0,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"State\":4}";
    private const string InspectJson =
        "{\"Found\":true,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptType\":0,\"State\":1,\"Amount\":100,\"Currency\":\"USD\",\"Provider\":\"stripe\",\"IdempotencyKey\":\"checkout:tenant-charge:91005:actor:91001:attempt:legacy-replay\",\"ProviderPaymentId\":\"pi_legacy_replay\",\"ProviderFenceToken\":\"11111111-1111-1111-1111-111111111111\",\"PreparedAtUtc\":\"2026-08-21T12:00:00Z\"}";
    private const string EventJson =
        "{\"Outcome\":0,\"ProviderInboxEventId\":91007,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptState\":2}";
    private const string ReconcileJson =
        "{\"Outcome\":0,\"ProviderInboxEventId\":91007,\"PortfolioId\":91000,\"TenantAccountId\":91004,\"PaymentAttemptId\":91006,\"AttemptState\":2,\"NextAttemptAtUtc\":null}";

    private static InteractiveProviderObject ProviderObject(string status, string key) => new(
        "pi_legacy_replay", status, key, PaymentIntentId: "pi_legacy_replay");

    private static InteractiveProviderObject ConfirmedNoProviderObject(string key) => new(
        string.Empty, "none", key, ConfirmedNoProviderObject: true);

    private static RentalCommandDbContext NewContext(string connectionString) => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connectionString).Options);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class NeverSandboxGuard : ISandboxGuard
    {
        public Task<bool> IsSandboxAsync(int? portfolioId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class FrozenProviderInboxClaimStore : IProviderInboxClaimStore
    {
        private int _claimCount;
        public int ClaimCount => Volatile.Read(ref _claimCount);

        public Task<IReadOnlyList<ProviderInboxClaim>> ClaimAsync(
            string claimOwner, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _claimCount);
            return Task.FromResult<IReadOnlyList<ProviderInboxClaim>>(
                [new ProviderInboxClaim(InboxId, "worker-legacy", ClaimToken, 1)]);
        }
    }

    private sealed class ReceiptAutopayProviderClient : IAutopayProviderClient
    {
        private int _createCount;
        private int _reconcileCount;
        public int CreateCount => Volatile.Read(ref _createCount);
        public int ReconcileCount => Volatile.Read(ref _reconcileCount);

        public Task<AutopayProviderPayment> CreateAsync(
            TenantPaymentAttempt attempt, string customerId, string paymentMethodId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _createCount);
            return Task.FromResult(new AutopayProviderPayment(
                "pi_legacy_replay", "succeeded", attempt.IdempotencyKey));
        }

        public Task<AutopayProviderPayment?> ReconcileAsync(
            TenantPaymentAttempt attempt, CancellationToken ct)
        {
            Interlocked.Increment(ref _reconcileCount);
            return Task.FromResult<AutopayProviderPayment?>(new(
                "pi_legacy_replay", "succeeded", attempt.IdempotencyKey));
        }
    }

    private sealed class ReceiptInteractiveProviderClient : IInteractivePaymentProviderClient
    {
        private readonly Dictionary<long, InteractiveProviderObject?> _reconciledByAttempt = [];
        private int _checkoutCreateCount;
        private int _paymentIntentCreateCount;
        private int _setupCreateCount;

        public InteractiveProviderObject? Reconciled { get; set; }
        public int CheckoutCreateCount => Volatile.Read(ref _checkoutCreateCount);
        public int PaymentIntentCreateCount => Volatile.Read(ref _paymentIntentCreateCount);
        public int SetupCreateCount => Volatile.Read(ref _setupCreateCount);

        public void SetReconciled(long attemptId, InteractiveProviderObject? provider) =>
            _reconciledByAttempt[attemptId] = provider;

        public Task<InteractiveProviderObject> CreatePaymentIntentAsync(
            InteractiveProviderCreateRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _paymentIntentCreateCount);
            return Task.FromResult(ProviderObject("open", request.IdempotencyKey));
        }

        public Task<InteractiveProviderObject> CreateCheckoutSessionAsync(
            InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _checkoutCreateCount);
            return Task.FromResult(ProviderObject("open", request.IdempotencyKey));
        }

        public Task<InteractiveProviderObject> CreateSetupCheckoutSessionAsync(
            InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _setupCreateCount);
            return Task.FromResult(ProviderObject("open", request.IdempotencyKey));
        }

        public Task<InteractiveProviderObject?> ReconcileAsync(
            InteractiveProviderAttempt attempt, CancellationToken ct) => Task.FromResult(
                _reconciledByAttempt.TryGetValue(attempt.PaymentAttemptId, out var provider)
                    ? provider
                    : Reconciled);

        public Task<InteractiveProviderObject?> CancelOrExpireAsync(
            InteractiveProviderAttempt attempt, CancellationToken ct) =>
            Task.FromResult(Reconciled);

        public Task<(string? CustomerId, string? PaymentMethodId)> GetSetupPaymentMethodAsync(
            string setupIntentId, CancellationToken ct) =>
            Task.FromResult<(string?, string?)>((null, null));
    }

    private sealed record Descriptor(
        string Operation, string Contract, WriteLockProtocol? Protocol, string[] Locks);
}
