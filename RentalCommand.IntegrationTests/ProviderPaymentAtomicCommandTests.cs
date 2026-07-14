using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;
using Stripe.Checkout;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for canonical autopay selection and provider-event idempotency.</summary>
public sealed class ProviderPaymentAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<RecordVerifiedProviderPaymentEventResult> EventCodec =
        new("record-verified-provider-payment-event-result.v1");
    private static readonly AtomicJsonResultCodec<ReconcileClaimedProviderPaymentEventResult> ReconcileCodec =
        new("reconcile-claimed-provider-payment-event-result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_provider_payments")
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
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            RecordVerifiedProviderPaymentEventCommand,
            RecordVerifiedProviderPaymentEventResult,
            RecordVerifiedProviderPaymentEventHandler>();
        services.AddAtomicCommandHandler<
            ReconcileClaimedProviderPaymentEventCommand,
            ReconcileClaimedProviderPaymentEventResult,
            ReconcileClaimedProviderPaymentEventHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await CreatePhysicalTestSchemaAsync(db);
    }

    private static async Task CreatePhysicalTestSchemaAsync(RentalCommandDbContext db)
    {
        // The foundation migration chain is intentionally temporary and will disappear at the
        // final baseline squash. Build the EF-owned tables directly, then install only the
        // canonical SQL objects exercised by provider receipt allocation and autopay selection.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
        foreach (var statement in TenantAccountPostgreSqlContract.CreateStatements)
            await db.Database.ExecuteSqlRawAsync(statement);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task CandidateSelection_IsOneTranslatedPostgreSqlQuery_AndSuppressesSubmittedAttempt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("candidate");

        await using (var db = NewContext())
        {
            var query = AutopayChargeService.BuildCandidateQuery(db);
            var sql = query.ToQueryString();
            sql.Should().Contain("NOT EXISTS");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            var candidate = await query.SingleAsync();
            candidate.TenantAccountId.Should().Be(scenario.AccountId);
            candidate.ChargeLedgerEntryId.Should().Be(scenario.ChargeId);
            candidate.EnrollmentId.Should().Be(scenario.EnrollmentId);
        }

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "autopay:tenant-charge:" + scenario.ChargeId,
                "pi_existing", TenantPaymentAttemptState.Submitted));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            (await AutopayChargeService.BuildCandidateQuery(db).CountAsync()).Should().Be(0);
        }
    }

    [SkippableFact]
    public async Task CanonicalProviderKeysAndOpenEnrollment_RejectDuplicates()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("constraints");

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "provider-key", "pi_unique",
                TenantPaymentAttemptState.Submitted));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "provider-key", "pi_other",
                TenantPaymentAttemptState.Submitted));
            await FluentActions.Invoking(() => db.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = NewContext())
        {
            db.TenantAutopayEnrollments.Add(new TenantAutopayEnrollment
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                AuthorizingPartyId = scenario.PartyId,
                Provider = "stripe",
                ProviderCustomerId = "cus_duplicate",
                ProviderPaymentMethodId = "pm_duplicate",
                EnrolledAtUtc = DateTime.UtcNow,
                CreatedByUserId = scenario.UserId,
            });
            await FluentActions.Invoking(() => db.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
        }
    }

    [SkippableFact]
    public async Task PortalStatusAndCancellation_UseCanonicalAccountEnrollment()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("portal");
        await using var db = NewContext();
        var service = new PortalService(db, new NoopLeaseQaService(), TimeProvider.System);

        var active = await service.GetAutopayStatusAsync(
            scenario.PortfolioId, scenario.TenantId, scenario.AccountId);
        active.Should().NotBeNull();
        active!.TenantAccountId.Should().Be(scenario.AccountId);
        active.Active.Should().BeTrue();

        var canceled = await service.CancelAutopayAsync(
            scenario.PortfolioId, scenario.TenantId, scenario.AccountId);
        canceled.Should().NotBeNull();
        canceled!.Active.Should().BeFalse();
        var enrollment = await db.TenantAutopayEnrollments.SingleAsync(row =>
            row.Id == scenario.EnrollmentId);
        enrollment.CanceledAtUtc.Should().NotBeNull();
        enrollment.CancelReason.Should().Be("Canceled by tenant");
    }

    [SkippableFact]
    public async Task CheckoutAndPaymentIntentTerminalEvents_ResolveOneAttemptAndPostOneReceipt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("webhook");
        const string paymentIntentId = "pi_checkout_terminal";
        long attemptId;
        await using (var db = NewContext())
        {
            var attempt = Attempt(scenario, "checkout:tenant-charge:" + scenario.ChargeId,
                paymentIntentId, TenantPaymentAttemptState.Submitted);
            attempt.NextAttemptAtUtc = DateTime.UtcNow.AddDays(1);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        StripePaymentService.ResolveCheckoutPaymentObjectId(new Session
        {
            Id = "cs_terminal",
            PaymentIntentId = paymentIntentId,
        }).Should().Be(paymentIntentId);

        foreach (var (eventId, eventType) in new[]
        {
            ("evt_checkout", "checkout.session.completed"),
            ("evt_intent", "payment_intent.succeeded"),
        })
        {
            var command = new RecordVerifiedProviderPaymentEventCommand(
                "stripe", eventId, eventType, "{}", paymentIntentId,
                ProviderPaymentEventKind.Succeeded, 100m, "usd", null,
                DateTime.UtcNow, DateTime.UtcNow);
            await Atomic.ExecuteAsync(
                new AtomicCommandIdentity("payments.provider-event.record", "stripe:" + eventId),
                command, EventCodec);
        }

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Succeeded);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().Be(1);
        (await verify.ProviderInboxEvents.CountAsync(row =>
            row.ProviderObjectId == paymentIntentId)).Should().Be(2);
    }

    [SkippableFact]
    public async Task ProviderInbox_TakeoverFencesStaleCompletionAndFailure_AndRecoversOnce()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("provider-inbox-fencing");
        const string succeededObjectId = "pi_fenced_success";
        const string failedObjectId = "pi_fenced_failure";
        long succeededAttemptId;
        long failedAttemptId;
        long succeededEventId;
        long failedEventId;

        await using (var db = NewContext())
        {
            var succeededAttempt = Attempt(scenario, "provider-inbox-success", succeededObjectId,
                TenantPaymentAttemptState.Submitted);
            var failedAttempt = Attempt(scenario, "provider-inbox-failure", failedObjectId,
                TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.AddRange(succeededAttempt, failedAttempt);
            await db.SaveChangesAsync();
            succeededAttemptId = succeededAttempt.Id;
            failedAttemptId = failedAttempt.Id;

            var databaseNow = await db.Database
                .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync();
            var succeededEvent = ProviderEvent(
                "evt_fenced_success", succeededObjectId, ProviderPaymentEventKind.Succeeded, databaseNow);
            var failedEvent = ProviderEvent(
                "evt_fenced_failure", failedObjectId, ProviderPaymentEventKind.Failed, databaseNow);
            db.ProviderInboxEvents.AddRange(succeededEvent, failedEvent);
            await db.SaveChangesAsync();
            succeededEventId = succeededEvent.Id;
            failedEventId = failedEvent.Id;
        }

        ProviderInboxClaim[] staleClaims;
        await using (var db = NewContext())
        {
            staleClaims = (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-a", TimeSpan.FromMinutes(2), 10))
                .ToArray();
            staleClaims.Should().HaveCount(2);
            staleClaims.Select(claim => claim.ClaimToken).Should().OnlyHaveUniqueItems();
        }

        await using (var db = NewContext())
        {
            (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", TimeSpan.FromMinutes(2), 10))
                .Should().BeEmpty("an unexpired claim token has one owner");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ProviderInboxEvents"
                SET "ClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" IN ({{succeededEventId}}, {{failedEventId}})
                """);
        }

        ProviderInboxClaim[] currentClaims;
        await using (var db = NewContext())
        {
            currentClaims = (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", TimeSpan.FromMinutes(2), 10))
                .ToArray();
            currentClaims.Should().HaveCount(2);
            currentClaims.Select(claim => claim.ClaimToken)
                .Should().NotIntersectWith(staleClaims.Select(claim => claim.ClaimToken));
            currentClaims.Should().OnlyContain(claim => claim.ClaimOwner == "worker-b");
        }

        foreach (var staleClaim in staleClaims)
        {
            await FluentActions.Awaiting(() => ReconcileAsync(staleClaim))
                .Should().ThrowAsync<AtomicReceiptInvariantException>(
                    "a reclaimed event cannot be completed or failed by its former token owner");
        }

        await using (var verify = NewContext())
        {
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == succeededAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Submitted);
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == failedAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Submitted);
            (await verify.TenantLedgerEntries.CountAsync(row =>
                row.ProviderPaymentAttemptId == succeededAttemptId)).Should().Be(0);
        }

        var currentSucceeded = currentClaims.Single(claim => claim.Id == succeededEventId);
        var currentFailed = currentClaims.Single(claim => claim.Id == failedEventId);
        (await ReconcileAsync(currentSucceeded)).Value.Outcome
            .Should().Be(ReconcileProviderPaymentEventOutcome.Applied);
        (await ReconcileAsync(currentFailed)).Value.Outcome
            .Should().Be(ReconcileProviderPaymentEventOutcome.Applied);

        var duplicateCommand = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_fenced_success", "payment_intent.succeeded", "{}", succeededObjectId,
            ProviderPaymentEventKind.Succeeded, 100m, "USD", null, DateTime.UtcNow, DateTime.UtcNow);
        var duplicate = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event.record", "stripe:evt_fenced_success"),
            duplicateCommand, EventCodec);
        var replay = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event.record", "stripe:evt_fenced_success"),
            duplicateCommand, EventCodec);
        duplicate.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Duplicate);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.ProviderInboxEventId.Should().Be(succeededEventId);

        await using (var verify = NewContext())
        {
            (await verify.ProviderInboxEvents.CountAsync(row =>
                row.Provider == "stripe" && row.ProviderEventId == "evt_fenced_success"))
                .Should().Be(1);
            (await verify.ProviderInboxEvents.CountAsync(row =>
                (row.Id == succeededEventId || row.Id == failedEventId)
                && row.ProcessedAtUtc != null
                && row.ClaimOwner == null
                && row.ClaimToken == null
                && row.ClaimExpiresAtUtc == null))
                .Should().Be(2, "only the replacement tokens finalize and release the durable events");
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == succeededAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Succeeded);
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == failedAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Failed);
            (await verify.TenantLedgerEntries.CountAsync(row =>
                row.ProviderPaymentAttemptId == succeededAttemptId)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == OutboxIdempotency.Create(
                    "provider-receipt", succeededAttemptId.ToString())))
                .Should().Be(1);
            var succeededAuditKey = $"{currentSucceeded.Id}:{currentSucceeded.ClaimToken:N}";
            var failedAuditKey = $"{currentFailed.Id}:{currentFailed.ClaimToken:N}";
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == "payments.provider-inbox.reconcile"
                && (row.CommandIdempotencyKey == succeededAuditKey
                    || row.CommandIdempotencyKey == failedAuditKey)
                && row.PortfolioId == scenario.PortfolioId
                && row.EntityType == nameof(TenantAccount)
                && row.EntityId == scenario.AccountId
                && row.Operation == AuditLogOperation.Updated
                && row.ActorLabel == "provider:worker:worker-b"
                && (row.ChangeReason == "Claimed provider event evt_fenced_success reconciled"
                    || row.ChangeReason == "Claimed provider event evt_fenced_failure reconciled")))
                .Should().Be(2, "each canonical terminal attempt produces one atomic audit event");
        }
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"provider-{suffix}@example.test",
            NormalizedUserName = $"PROVIDER-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"provider-{suffix}@example.test",
            NormalizedEmail = $"PROVIDER-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Provider {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"Provider {suffix}", ManagementCompanyName = $"Provider {suffix}",
            TimeZone = "UTC", Currency = "USD", CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Add(accessContext);
        await db.SaveChangesAsync();
        var property = new Property
        {
            PortfolioId = portfolio.Id, Name = $"Property {suffix}", AddressLine1 = "1 Pay Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = portfolio.Id, Property = property, UnitNumber = "1",
            CreatedAt = now, UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id, FirstName = "Tenant", LastName = suffix,
            CreatedAt = now, UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id, Name = $"Template {suffix}", Version = 1,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.AddRange(property, unit, tenant, template);
        await db.SaveChangesAsync();
        var documentSourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{template.Id}:v{template.Version}",
            DocumentTemplateId = template.Id, DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay", RendererVersion = 1,
            SnapshotPayload = "{}", CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        db.Add(documentSourceVersion);
        await db.SaveChangesAsync();
        var relationship = new LeaseManagement
        {
            PortfolioId = portfolio.Id, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}", CreatedAtUtc = now, UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.Add(relationship);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PortfolioId = portfolio.Id, LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{suffix}", Currency = "USD", OpenedAtUtc = now,
            CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id, LeaseManagementId = relationship.Id, TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant, EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "integration", CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id, VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}", ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddMonths(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 100m, RentDueDay = 1, SecurityDepositObligation = 0m,
            LateFeeAmount = 0m, GracePeriodDays = 0, Currency = "USD",
            TermsSchemaVersion = 1, TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersion.Id,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = user.Id,
        };
        db.AddRange(account, party, agreement);
        await db.SaveChangesAsync();
        var enrollment = new TenantAutopayEnrollment
        {
            PortfolioId = portfolio.Id, TenantAccountId = account.Id, AuthorizingPartyId = party.Id,
            Provider = "stripe", ProviderCustomerId = $"cus_{suffix}",
            ProviderPaymentMethodId = $"pm_{suffix}", EnrolledAtUtc = now, CreatedByUserId = user.Id,
        };
        var access = new TenantUserAccess
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id, LeaseManagementPartyId = party.Id,
            GrantedAtUtc = now, GrantedByUserId = user.Id, Reason = "integration portal access",
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = portfolio.Id, TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge, Direction = TenantLedgerDirection.Debit,
            Amount = 100m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now), PostedAtUtc = now, Description = "Rent",
            BusinessKey = $"rent:{suffix}", LeaseAgreementId = agreement.Id,
            CreatedByUserId = user.Id,
        };
        db.AddRange(access, enrollment, charge);
        await db.SaveChangesAsync();
        return new Scenario(
            portfolio.Id, account.Id, party.Id, enrollment.Id, charge.Id, user.Id, tenant.Id);
    }

    private static TenantPaymentAttempt Attempt(
        Scenario scenario, string key, string providerObjectId, TenantPaymentAttemptState state) => new()
    {
        PortfolioId = scenario.PortfolioId,
        TenantAccountId = scenario.AccountId,
        Provider = "stripe",
        ProviderObjectId = providerObjectId,
        IdempotencyKey = key,
        AttemptType = TenantPaymentAttemptType.Charge,
        State = state,
        Amount = 100m,
        Currency = "USD",
        PreparedAtUtc = DateTime.UtcNow,
        SubmittedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = scenario.UserId,
    };

    private static ProviderInboxEvent ProviderEvent(
        string eventId,
        string providerObjectId,
        ProviderPaymentEventKind eventKind,
        DateTime databaseNow) => new()
    {
        Provider = "stripe",
        ProviderEventId = eventId,
        EventType = eventKind == ProviderPaymentEventKind.Succeeded
            ? "payment_intent.succeeded"
            : "payment_intent.payment_failed",
        Payload = "{}",
        ProviderObjectId = providerObjectId,
        EventKind = eventKind,
        Amount = 100m,
        Currency = "USD",
        FailureReason = eventKind == ProviderPaymentEventKind.Failed ? "Provider declined payment." : null,
        OccurredAtUtc = databaseNow,
        ReceivedAtUtc = databaseNow,
        NextAttemptAtUtc = databaseNow,
    };

    private Task<AtomicCommandOutcome<ReconcileClaimedProviderPaymentEventResult>> ReconcileAsync(
        ProviderInboxClaim claim) => Atomic.ExecuteAsync(
        new AtomicCommandIdentity(
            "payments.provider-inbox.reconcile", $"{claim.Id}:{claim.ClaimToken:N}"),
        new ReconcileClaimedProviderPaymentEventCommand(
            claim.Id, claim.ClaimOwner, claim.ClaimToken, DateTime.UtcNow),
        ReconcileCodec);

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString()).Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for provider-payment PostgreSQL tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:provider-payment";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId, int AccountId, int PartyId, int EnrollmentId, long ChargeId,
        int UserId, int TenantId);
}
