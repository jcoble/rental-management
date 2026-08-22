using System.Text.Json;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Api.Writes;
using RentalCommand.Data;
using RentalCommand.Data.Payments;

namespace RentalCommand.Api.Services.Payments;

/// <inheritdoc cref="IStripePaymentService"/>
public class StripePaymentService : IStripePaymentService
{
    // Canonical account/attempt facts carried back to the signature-verified webhook.
    private const string MetadataTenantAccountId = "tenantAccountId";
    private const string MetadataChargeLedgerEntryId = "chargeLedgerEntryId";
    private const string MetadataPaymentAttemptId = "paymentAttemptId";
    private const string MetadataPortfolioId = "portfolioId";
    private const string MetadataAuthorizingPartyId = "authorizingPartyId";
    private const string MetadataActorUserId = "actorUserId";
    private const string MetadataTenantId = "tenantId";
    private const string MetadataAutopay = "autopay";

    private const string DefaultSuccessUrl = "/portal/payments?checkout=success";
    private const string DefaultCancelUrl = "/portal/payments?checkout=cancel";

    private readonly StripeConfig _config;
    private readonly ISandboxGuard _sandbox;
    private readonly ILogger<StripePaymentService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;
    private readonly IInteractivePaymentProviderClient _interactiveProvider;

    public StripePaymentService(
        IOptions<StripeConfig> config,
        ISandboxGuard sandbox,
        ILogger<StripePaymentService> logger,
        TimeProvider timeProvider,
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        IInteractivePaymentProviderClient? interactiveProvider = null)
    {
        _config = config.Value;
        _sandbox = sandbox;
        _logger = logger;
        _timeProvider = timeProvider;
        _db = db;
        _writes = writes;
        _interactiveProvider = interactiveProvider ?? new StripeInteractivePaymentProviderClient(config);
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnlinePaymentsAvailableAsync(int portfolioId, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            return false;
        }

        return !await _sandbox.IsSandboxAsync(portfolioId, ct);
    }

    /// <inheritdoc/>
    public async Task<CreateIntentResult> CreatePaymentIntentAsync(
        int portfolioId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId, CancellationToken ct,
        string? attemptKey = null)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — create-intent is a no-op");
            return CreateIntentResult.NotEnabled();
        }

        // HARD sandbox guard: never move real money for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] create-intent for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CreateIntentResult.NotEnabled();
        }

        var idempotencyKey = BuildPaymentIdempotencyKey(
            "intent", chargeLedgerEntryId, actorUserId, attemptKey);
        var prepareCommand = new PrepareProviderPaymentCreateCommand(
                portfolioId,
                tenantAccountId,
                chargeLedgerEntryId,
                actorUserId,
                TenantId: null,
                AutopayEnrollmentId: null,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "USD",
                PreparedAtUtc: _timeProvider.UtcNow());
        var prepared = await _writes.ExecuteAsync(idempotencyKey,
            ProviderPaymentWriteSupport.Write<PrepareProviderPaymentCreateCommand,
                PrepareProviderPaymentCreateResult>(_db, "payments.provider-create.prepare", prepareCommand), ct);
        if (prepared.Value.Outcome == PrepareProviderPaymentCreateOutcome.NotFound)
        {
            return CreateIntentResult.NotFound();
        }

        var reconciledPrepared = await ReconcilePreparedAttemptBeforeSubmitAsync(
            prepared.Value, portfolioId, tenantAccountId, ct);
        if (reconciledPrepared is not null)
            return CreateIntentStatus(reconciledPrepared);

        var submitted = await SubmitProviderAttemptAsync(prepared.Value, portfolioId, tenantAccountId, ct);
        var submitResult = submitted.Value;
        if (submitResult.Outcome != SubmitProviderPaymentCreateOutcome.Submitted)
        {
            if (submitResult.Outcome == SubmitProviderPaymentCreateOutcome.AlreadySubmitted)
                submitResult = await ReconcileInteractiveAttemptAsync(submitResult, ct);
            return CreateIntentStatus(submitResult);
        }

        // The atomic Submit receipt is the durable cross-process owner for the provider boundary.
        // A replay can only reconcile the exact stored key; it must never independently create.
        if (submitted.Disposition == AtomicCommandDisposition.Replayed)
        {
            submitResult = await ReconcileInteractiveAttemptAsync(submitResult, ct);
            return CreateIntentStatus(submitResult);
        }

        InteractiveProviderObject intent;
        try
        {
            intent = await _interactiveProvider.CreatePaymentIntentAsync(
                new InteractiveProviderCreateRequest(
                    submitResult.PaymentAttemptId, submitResult.Provider,
                    submitResult.IdempotencyKey, portfolioId, tenantAccountId,
                    chargeLedgerEntryId, submitResult.Amount, submitResult.Currency, IsSetup: false), ct);
        }
        catch (InteractiveProviderException ex) when (ex.IsDefinitive)
        {
            await FailInteractiveAttemptAsync(submitResult, ex, ct);
            return CreateIntentResult.Failed(submitResult.PaymentAttemptId);
        }

        var intentState = MapProviderState(intent.Status);
        await FinalizeInteractiveAttemptAsync(submitResult, intent, intentState, ct);

        _logger.LogInformation(
            "Created Stripe PaymentIntent {IntentId} for tenant account {TenantAccountId} charge {ChargeId} (portfolio {PortfolioId})",
            intent.ProviderPaymentId, tenantAccountId, chargeLedgerEntryId, portfolioId);

        return intentState == TenantPaymentAttemptState.Succeeded
            ? CreateIntentResult.Succeeded(submitResult.PaymentAttemptId, ProviderObjectId(intent))
            : intentState == TenantPaymentAttemptState.Canceled
                ? CreateIntentResult.Canceled(submitResult.PaymentAttemptId)
                : intentState == TenantPaymentAttemptState.Failed
                    ? CreateIntentResult.Failed(submitResult.PaymentAttemptId)
                    : CreateIntentResult.Ok(
                        intent.ClientSecret!,
                        _config.PublishableKey,
                        submitResult.PaymentAttemptId);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreatePaymentCheckoutSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId,
        string? successUrl, string? cancelUrl, CancellationToken ct, string? attemptKey = null)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — payment checkout is a no-op");
            return CheckoutResult.NotEnabled();
        }

        // HARD sandbox guard: never create a real Checkout session for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] payment checkout for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CheckoutResult.NotEnabled();
        }

        var idempotencyKey = BuildPaymentIdempotencyKey(
            "checkout", chargeLedgerEntryId, actorUserId, attemptKey);
        var prepareCommand = new PrepareProviderPaymentCreateCommand(
                portfolioId,
                tenantAccountId,
                chargeLedgerEntryId,
                actorUserId,
                tenantId,
                AutopayEnrollmentId: null,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "USD",
                PreparedAtUtc: _timeProvider.UtcNow());
        var prepared = await _writes.ExecuteAsync(idempotencyKey,
            ProviderPaymentWriteSupport.Write<PrepareProviderPaymentCreateCommand,
                PrepareProviderPaymentCreateResult>(_db, "payments.provider-create.prepare", prepareCommand), ct);
        if (prepared.Value.Outcome == PrepareProviderPaymentCreateOutcome.NotFound)
        {
            return CheckoutResult.NotFound();
        }

        var reconciledPrepared = await ReconcilePreparedAttemptBeforeSubmitAsync(
            prepared.Value, portfolioId, tenantAccountId, ct);
        if (reconciledPrepared is not null)
            return CheckoutStatus(reconciledPrepared);

        var submitted = await SubmitProviderAttemptAsync(prepared.Value, portfolioId, tenantAccountId, ct);
        var submitResult = submitted.Value;
        if (submitResult.Outcome != SubmitProviderPaymentCreateOutcome.Submitted)
        {
            if (submitResult.Outcome == SubmitProviderPaymentCreateOutcome.AlreadySubmitted)
                submitResult = await ReconcileInteractiveAttemptAsync(submitResult, ct);
            return CheckoutStatus(submitResult);
        }

        // The atomic Submit receipt is the durable cross-process owner for the provider boundary.
        // A replay can only reconcile the exact stored key; it must never independently create.
        if (submitted.Disposition == AtomicCommandDisposition.Replayed)
        {
            submitResult = await ReconcileInteractiveAttemptAsync(submitResult, ct);
            return CheckoutStatus(submitResult);
        }

        var resolvedSuccessUrl = ResolveSuccessUrl(successUrl, submitResult.PaymentAttemptId,
            chargeLedgerEntryId);
        var resolvedCancelUrl = ResolveCancelUrl(cancelUrl, submitResult.PaymentAttemptId,
            chargeLedgerEntryId);
        InteractiveProviderObject session;
        try
        {
            session = await _interactiveProvider.CreateCheckoutSessionAsync(
                new InteractiveProviderCreateRequest(
                    submitResult.PaymentAttemptId, submitResult.Provider,
                    submitResult.IdempotencyKey, portfolioId, tenantAccountId,
                    chargeLedgerEntryId, submitResult.Amount, submitResult.Currency, IsSetup: false),
                resolvedSuccessUrl, resolvedCancelUrl, ct);
        }
        catch (InteractiveProviderException ex) when (ex.IsDefinitive)
        {
            await FailInteractiveAttemptAsync(submitResult, ex, ct);
            return CheckoutResult.Failed(submitResult.PaymentAttemptId);
        }

        var sessionState = MapProviderState(session.Status);
        await FinalizeInteractiveAttemptAsync(submitResult, session, sessionState, ct);

        _logger.LogInformation(
            "Created Stripe Checkout session {SessionId} for tenant account {TenantAccountId} charge {ChargeId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.CheckoutSessionId ?? session.ProviderPaymentId, tenantAccountId, chargeLedgerEntryId, portfolioId, tenantId);

        return sessionState == TenantPaymentAttemptState.Succeeded
            ? CheckoutResult.Succeeded(submitResult.PaymentAttemptId, ProviderObjectId(session))
            : sessionState == TenantPaymentAttemptState.Canceled
                ? CheckoutResult.Canceled(submitResult.PaymentAttemptId)
                : sessionState == TenantPaymentAttemptState.Failed
                    ? CheckoutResult.Failed(submitResult.PaymentAttemptId)
                    : CheckoutResult.Ok(session.CheckoutUrl!, submitResult.PaymentAttemptId);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreateAutopaySetupSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, int actorUserId, string operationKey,
        string? successUrl, string? cancelUrl, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — autopay setup is a no-op");
            return CheckoutResult.NotEnabled();
        }

        // HARD sandbox guard: never enroll a real payment method for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] autopay setup for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CheckoutResult.NotEnabled();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var idempotencyKey = $"autopay-setup:{operationKey.Trim()}";
        if (idempotencyKey.Length > 200)
            throw new ArgumentException("Autopay setup operation key cannot exceed 186 characters.", nameof(operationKey));
        var prepareCommand = new PrepareProviderAutopaySetupCommand(portfolioId, tenantAccountId, tenantId,
            actorUserId, "stripe", idempotencyKey, "USD", _timeProvider.UtcNow());
        var prepared = await _writes.ExecuteAsync(idempotencyKey,
            ProviderPaymentWriteSupport.Write<PrepareProviderAutopaySetupCommand,
                PrepareProviderAutopaySetupResult>(_db, "payments.provider-autopay.prepare", prepareCommand), ct);
        if (prepared.Value.Outcome == PrepareProviderAutopaySetupOutcome.NotFound)
        {
            return CheckoutResult.NotFound();
        }

        var reconciledPrepared = await ReconcilePreparedSetupAttemptBeforeSubmitAsync(
            prepared.Value, portfolioId, tenantAccountId, ct);
        if (reconciledPrepared is not null)
            return CheckoutStatus(reconciledPrepared);

        var submitted = await SubmitProviderAttemptAsync(
            prepared.Value, portfolioId, tenantAccountId, ct);
        var submitResult = submitted.Value;
        if (submitResult.Outcome != SubmitProviderPaymentCreateOutcome.Submitted)
        {
            if (submitResult.Outcome == SubmitProviderPaymentCreateOutcome.AlreadySubmitted)
                submitResult = await ReconcileInteractiveSetupAttemptAsync(submitResult, ct);
            return CheckoutStatus(submitResult);
        }

        // The atomic Submit receipt is the durable cross-process owner for the provider boundary.
        // A replay can only reconcile the exact stored key; it must never independently create.
        if (submitted.Disposition == AtomicCommandDisposition.Replayed)
        {
            submitResult = await ReconcileInteractiveSetupAttemptAsync(submitResult, ct);
            return CheckoutStatus(submitResult);
        }

        var resolvedSuccessUrl = ResolveSuccessUrl(successUrl, submitResult.PaymentAttemptId, null);
        var resolvedCancelUrl = ResolveCancelUrl(cancelUrl, submitResult.PaymentAttemptId, null);
        InteractiveProviderObject session;
        try
        {
            session = await _interactiveProvider.CreateSetupCheckoutSessionAsync(
                new InteractiveProviderCreateRequest(
                    submitResult.PaymentAttemptId, submitResult.Provider,
                    submitResult.IdempotencyKey, portfolioId, tenantAccountId,
                    null, 0m, submitResult.Currency, IsSetup: true,
                    TenantId: tenantId,
                    AuthorizingPartyId: prepared.Value.AuthorizingPartyId,
                    ActorUserId: actorUserId),
                resolvedSuccessUrl, resolvedCancelUrl, ct);
        }
        catch (InteractiveProviderException ex) when (ex.IsDefinitive)
        {
            await FailInteractiveAttemptAsync(submitResult, ex, ct);
            return CheckoutResult.Failed(submitResult.PaymentAttemptId);
        }

        var setupState = MapProviderState(session.Status);
        await FinalizeInteractiveAttemptAsync(submitResult, session, setupState, ct);

        _logger.LogInformation(
            "Created Stripe autopay setup session {SessionId} for tenant account {TenantAccountId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.CheckoutSessionId ?? session.ProviderPaymentId, tenantAccountId, portfolioId, tenantId);

        return setupState == TenantPaymentAttemptState.Succeeded
            ? CheckoutResult.Succeeded(submitResult.PaymentAttemptId, ProviderObjectId(session))
            : setupState == TenantPaymentAttemptState.Canceled
                ? CheckoutResult.Canceled(submitResult.PaymentAttemptId)
                : setupState == TenantPaymentAttemptState.Failed
                    ? CheckoutResult.Failed(submitResult.PaymentAttemptId)
                    : CheckoutResult.Ok(session.CheckoutUrl!, submitResult.PaymentAttemptId);
    }

    /// <inheritdoc/>
    public async Task HandleWebhookEventAsync(string json, string signature, CancellationToken ct)
    {
        // Throws StripeException on bad signature — let it propagate to the controller (→ 400).
        // throwOnApiVersionMismatch: false so a Stripe account pinned to a different API version than
        // this SDK build doesn't hard-fail every webhook. We only read a small, stable set of fields
        // (event id/type, session/intent id, payment_status, metadata) that are version-agnostic.
        var ev = EventUtility.ConstructEvent(json, signature, _config.WebhookSecret, throwOnApiVersionMismatch: false);
        var now = _timeProvider.UtcNow();
        var command = await NormalizeVerifiedEventAsync(ev, now, ct);
        var key = $"stripe:{ev.Id}";
        var outcome = await _writes.ExecuteExactAsync(key,
            ProviderPaymentWriteSupport.Write<RecordVerifiedProviderPaymentEventCommand,
                RecordVerifiedProviderPaymentEventResult>(_db, "payments.provider-event.record", command), ct);

        _logger.LogInformation(
            "Stripe webhook event {EventId} ({EventType}) completed with {Outcome}.",
            ev.Id,
            ev.Type,
            outcome.Value.Outcome);
    }

    private async Task<RecordVerifiedProviderPaymentEventCommand> NormalizeVerifiedEventAsync(
        Event ev,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (ev.Data.Object is PaymentIntent intent &&
            ev.Type is "payment_intent.succeeded" or "payment_intent.payment_failed" or "payment_intent.canceled")
        {
            var kind = ev.Type switch
            {
                "payment_intent.succeeded" => ProviderPaymentEventKind.Succeeded,
                "payment_intent.payment_failed" => ProviderPaymentEventKind.Failed,
                _ => ProviderPaymentEventKind.Canceled,
            };
            return new RecordVerifiedProviderPaymentEventCommand(
                "stripe",
                ev.Id,
                ev.Type,
                JsonSerializer.Serialize(new
                {
                    intent.Id,
                    intent.Status,
                    intent.Amount,
                    intent.Currency,
                }),
                intent.Id,
                kind,
                intent.Amount / 100m,
                intent.Currency,
                intent.LastPaymentError?.Message,
                    receivedAtUtc,
                    receivedAtUtc,
                    EnrollmentPaymentAttemptId: intent.Metadata.TryGetValue(
                    MetadataPaymentAttemptId, out var intentPaymentAttemptRaw)
                    && long.TryParse(intentPaymentAttemptRaw, out var intentPaymentAttemptId)
                    ? intentPaymentAttemptId
                    : null);
        }

        if (ev.Data.Object is Session session
            && ev.Type is "checkout.session.completed" or "checkout.session.expired")
        {
            var metadata = session.Metadata ?? new Dictionary<string, string>();
            var isSetup = session.Mode == "setup" ||
                (metadata.TryGetValue(MetadataAutopay, out var autopay) && autopay == "1");
            metadata.TryGetValue(MetadataPaymentAttemptId, out var paymentAttemptRaw);
            _ = long.TryParse(paymentAttemptRaw, out var paymentAttemptId);
            var eventKind = ev.Type == "checkout.session.expired"
                ? ProviderPaymentEventKind.Canceled
                : string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
                    ? ProviderPaymentEventKind.Succeeded
                    : ProviderPaymentEventKind.Pending;
            if (!isSetup)
            {
                return new RecordVerifiedProviderPaymentEventCommand(
                    "stripe",
                    ev.Id,
                    ev.Type,
                    JsonSerializer.Serialize(new
                    {
                        session.Id,
                        session.PaymentIntentId,
                        session.PaymentStatus,
                        session.Mode,
                    }),
                    string.IsNullOrWhiteSpace(session.PaymentIntentId) ? session.Id : session.PaymentIntentId,
                    eventKind,
                    session.AmountTotal is long amount ? amount / 100m : null,
                    session.Currency,
                    null,
                    receivedAtUtc,
                    receivedAtUtc,
                    EnrollmentPaymentAttemptId: paymentAttemptId > 0 ? paymentAttemptId : null);
            }

            metadata.TryGetValue(MetadataTenantAccountId, out var accountRaw);
            metadata.TryGetValue(MetadataAuthorizingPartyId, out var partyRaw);
            metadata.TryGetValue(MetadataActorUserId, out var actorRaw);
            metadata.TryGetValue(MetadataPaymentAttemptId, out var attemptRaw);
            metadata.TryGetValue(MetadataTenantId, out var tenantRaw);
            metadata.TryGetValue(MetadataPortfolioId, out var portfolioRaw);
            _ = int.TryParse(accountRaw, out var tenantAccountId);
            _ = int.TryParse(partyRaw, out var authorizingPartyId);
            _ = int.TryParse(actorRaw, out var actorUserId);
            _ = long.TryParse(attemptRaw, out var setupPaymentAttemptId);
            _ = int.TryParse(tenantRaw, out var tenantId);
            _ = int.TryParse(portfolioRaw, out var portfolioId);

            string? customerId = session.CustomerId;
            string? paymentMethodId = null;
            if (ev.Type == "checkout.session.completed"
                && !string.IsNullOrWhiteSpace(session.SetupIntentId))
            {
                var setupFacts = await _interactiveProvider.GetSetupPaymentMethodAsync(
                    session.SetupIntentId, ct);
                paymentMethodId = setupFacts.PaymentMethodId;
                customerId ??= setupFacts.CustomerId;
            }

            return new RecordVerifiedProviderPaymentEventCommand(
                "stripe",
                ev.Id,
                ev.Type,
                JsonSerializer.Serialize(new
                {
                    session.Id,
                    session.SetupIntentId,
                    session.Mode,
                    portfolioId,
                    tenantAccountId,
                    authorizingPartyId,
                    actorUserId,
                    setupPaymentAttemptId,
                    tenantId,
                }),
                session.Id,
                ev.Type == "checkout.session.expired"
                    ? ProviderPaymentEventKind.Canceled
                    : ProviderPaymentEventKind.SetupCompleted,
                null,
                null,
                null,
                receivedAtUtc,
                receivedAtUtc,
                portfolioId > 0 ? portfolioId : null,
                tenantAccountId > 0 ? tenantAccountId : null,
                authorizingPartyId > 0 ? authorizingPartyId : null,
                actorUserId > 0 ? actorUserId : null,
                setupPaymentAttemptId > 0 ? setupPaymentAttemptId : null,
                customerId,
                paymentMethodId);
        }

        return new RecordVerifiedProviderPaymentEventCommand(
            "stripe",
            ev.Id,
            ev.Type,
            JsonSerializer.Serialize(new { ev.Id, ev.Type }),
            $"event:{ev.Id}",
            ProviderPaymentEventKind.Ignored,
            null,
            null,
            null,
            receivedAtUtc,
            receivedAtUtc);
    }

    internal static string ResolveCheckoutPaymentObjectId(Session session) =>
        !string.IsNullOrWhiteSpace(session.PaymentIntentId)
            ? session.PaymentIntentId
            : throw new InvalidOperationException(
                $"Stripe Checkout session {session.Id} did not expose its PaymentIntent identity.");

    private static CreateIntentResult CreateIntentStatus(SubmitProviderPaymentCreateResult result) =>
        result.State switch
        {
            TenantPaymentAttemptState.Succeeded => CreateIntentResult.Succeeded(
                result.PaymentAttemptId, result.ProviderPaymentId),
            TenantPaymentAttemptState.Canceled => CreateIntentResult.Canceled(result.PaymentAttemptId),
            TenantPaymentAttemptState.Failed => CreateIntentResult.Failed(result.PaymentAttemptId),
            _ => CreateIntentResult.Pending(result.PaymentAttemptId,
                result.State.ToString(), result.ProviderPaymentId),
        };

    private static CheckoutResult CheckoutStatus(SubmitProviderPaymentCreateResult result) =>
        result.State switch
        {
            TenantPaymentAttemptState.Succeeded => CheckoutResult.Succeeded(
                result.PaymentAttemptId, result.ProviderPaymentId),
            TenantPaymentAttemptState.Canceled => CheckoutResult.Canceled(result.PaymentAttemptId),
            TenantPaymentAttemptState.Failed => CheckoutResult.Failed(result.PaymentAttemptId),
            _ => CheckoutResult.Pending(result.PaymentAttemptId,
                result.State.ToString(), result.ProviderPaymentId),
        };

    private async Task<SubmitProviderPaymentCreateResult?> ReconcilePreparedAttemptBeforeSubmitAsync(
        PrepareProviderPaymentCreateResult prepared, int portfolioId, int tenantAccountId,
        CancellationToken ct)
    {
        if (prepared.State != TenantPaymentAttemptState.Prepared)
            return prepared.State == TenantPaymentAttemptState.Succeeded
                || prepared.State == TenantPaymentAttemptState.Canceled
                || prepared.State == TenantPaymentAttemptState.Failed
                ? ToSubmitResult(prepared)
                : null;

        var provider = await _interactiveProvider.ReconcileAsync(ToInteractiveAttempt(prepared), ct);
        if (provider is null)
            return ToSubmitResult(prepared); // Unknown lookup result is not permission to create a second object.
        if (provider.ConfirmedNoProviderObject)
            return null; // The only path that may continue to Submit/create.

        var state = MapProviderState(provider.Status);
        // Re-play the durable submit command before every provider-object finalization. The
        // prepare receipt may be an old cached result without the fence token that the first
        // submit committed; replaying submit recovers the exact current token and prevents a
        // response-lost retry from producing a different finalize payload.
        var submission = await SubmitProviderAttemptAsync(prepared, portfolioId, tenantAccountId, ct);
        var submit = submission.Value;
        if (submit.State is TenantPaymentAttemptState.Succeeded
            or TenantPaymentAttemptState.Canceled
            or TenantPaymentAttemptState.Failed)
            return submit;
        await FinalizeInteractiveAttemptAsync(submit, provider, state, ct);
        return submit with
        {
            Outcome = OutcomeForState(state),
            State = state,
            ProviderPaymentId = ProviderObjectId(provider),
        };
    }

    private async Task<SubmitProviderPaymentCreateResult?> ReconcilePreparedSetupAttemptBeforeSubmitAsync(
        PrepareProviderAutopaySetupResult prepared, int portfolioId, int tenantAccountId,
        CancellationToken ct)
    {
        if (prepared.State != TenantPaymentAttemptState.Prepared)
            return prepared.State == TenantPaymentAttemptState.Succeeded
                || prepared.State == TenantPaymentAttemptState.Canceled
                || prepared.State == TenantPaymentAttemptState.Failed
                ? ToSubmitResult(prepared)
                : null;

        var provider = await _interactiveProvider.ReconcileAsync(ToInteractiveAttempt(prepared), ct);
        if (provider is null)
            return ToSubmitResult(prepared);
        if (provider.ConfirmedNoProviderObject)
            return null;

        var state = MapProviderState(provider.Status);
        var submission = await SubmitProviderAttemptAsync(prepared, portfolioId, tenantAccountId, ct);
        var submit = submission.Value;
        if (submit.State is TenantPaymentAttemptState.Succeeded
            or TenantPaymentAttemptState.Canceled
            or TenantPaymentAttemptState.Failed)
            return submit;
        await FinalizeInteractiveAttemptAsync(submit, provider, state, ct);
        return submit with
        {
            Outcome = OutcomeForState(state),
            State = state,
            ProviderPaymentId = ProviderObjectId(provider),
        };
    }

    private async Task<SubmitProviderPaymentCreateResult> ReconcileInteractiveAttemptAsync(
        SubmitProviderPaymentCreateResult attempt, CancellationToken ct)
    {
        if (attempt.State != TenantPaymentAttemptState.Submitted)
            return attempt;

        var provider = await _interactiveProvider.ReconcileAsync(ToInteractiveAttempt(attempt), ct);
        if (provider is not null && !provider.ConfirmedNoProviderObject)
        {
            var state = MapProviderState(provider.Status);
            await FinalizeInteractiveAttemptAsync(attempt, provider, state, ct);
            return attempt with
            {
                Outcome = OutcomeForState(state),
                State = state,
                ProviderPaymentId = ProviderObjectId(provider),
            };
        }

        if (provider?.ConfirmedNoProviderObject == true
            && attempt.PreparedAtUtc is DateTime preparedAt
            && preparedAt <= _timeProvider.UtcNow().AddHours(-24))
        {
            var command = new FailProviderPaymentCreateCommand(
                    attempt.PortfolioId, attempt.TenantAccountId, attempt.PaymentAttemptId,
                    attempt.Provider, attempt.IdempotencyKey, "PROVIDER_RECONCILE_EXPIRED",
                    "Provider reconciliation found no accepted payment after 24 hours.",
                    _timeProvider.UtcNow(), attempt.ProviderFenceToken);
            await _writes.ExecuteAsync(attempt.IdempotencyKey,
                ProviderPaymentWriteSupport.Write<FailProviderPaymentCreateCommand,
                    FailProviderPaymentCreateResult>(_db, "payments.provider-create.fail", command), ct);
            return attempt with
            {
                Outcome = SubmitProviderPaymentCreateOutcome.Failed,
                State = TenantPaymentAttemptState.Failed,
            };
        }

        return attempt;
    }

    private async Task<SubmitProviderPaymentCreateResult> ReconcileInteractiveSetupAttemptAsync(
        SubmitProviderPaymentCreateResult attempt, CancellationToken ct)
    {
        if (attempt.State != TenantPaymentAttemptState.Submitted)
            return attempt;

        var provider = await _interactiveProvider.ReconcileAsync(ToInteractiveAttempt(attempt), ct);
        if (provider is not null && !provider.ConfirmedNoProviderObject)
        {
            var state = MapProviderState(provider.Status);
            await FinalizeInteractiveAttemptAsync(attempt, provider, state, ct);
            return attempt with
            {
                Outcome = OutcomeForState(state),
                State = state,
                ProviderPaymentId = ProviderObjectId(provider),
            };
        }

        if (provider?.ConfirmedNoProviderObject == true
            && attempt.PreparedAtUtc is DateTime preparedAt
            && preparedAt <= _timeProvider.UtcNow().AddHours(-24))
        {
            var command = new FailProviderPaymentCreateCommand(
                    attempt.PortfolioId, attempt.TenantAccountId, attempt.PaymentAttemptId,
                    attempt.Provider, attempt.IdempotencyKey, "PROVIDER_RECONCILE_EXPIRED",
                    "Provider reconciliation found no accepted setup after 24 hours.",
                    _timeProvider.UtcNow(), attempt.ProviderFenceToken);
            await _writes.ExecuteAsync(attempt.IdempotencyKey,
                ProviderPaymentWriteSupport.Write<FailProviderPaymentCreateCommand,
                    FailProviderPaymentCreateResult>(_db, "payments.provider-create.fail", command), ct);
            return attempt with
            {
                Outcome = SubmitProviderPaymentCreateOutcome.Failed,
                State = TenantPaymentAttemptState.Failed,
            };
        }

        return attempt;
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CancelPaymentAttemptAsync(
        int portfolioId, int tenantId, int tenantAccountId, long paymentAttemptId,
        string reason, CancellationToken ct)
    {
        if (!_config.Enabled)
            return CheckoutResult.NotEnabled();

        var command = new InspectProviderPaymentAttemptCommand(
            portfolioId, tenantId, tenantAccountId, paymentAttemptId, "stripe");
        var inspected = await _writes.ExecuteAsync(paymentAttemptId.ToString(),
            ProviderPaymentWriteSupport.Write<InspectProviderPaymentAttemptCommand,
                InspectProviderPaymentAttemptResult>(_db, "payments.provider-attempt.inspect", command), ct);
        var attempt = inspected.Value;
        if (!attempt.Found)
            return CheckoutResult.NotFound();

        var submit = ToSubmitResult(attempt);
        if (attempt.State == TenantPaymentAttemptState.Succeeded)
            return CheckoutResult.Succeeded(paymentAttemptId, attempt.ProviderPaymentId);
        if (attempt.State == TenantPaymentAttemptState.Canceled)
            return CheckoutResult.Canceled(paymentAttemptId);
        if (attempt.State == TenantPaymentAttemptState.Failed)
            return CheckoutResult.Failed(paymentAttemptId);

        var provider = await _interactiveProvider.ReconcileAsync(ToInteractiveAttempt(attempt), ct);
        if (provider is null)
            return CheckoutResult.Pending(paymentAttemptId, attempt.State.ToString(), attempt.ProviderPaymentId);

        if (!provider.ConfirmedNoProviderObject
            && MapProviderState(provider.Status) == TenantPaymentAttemptState.Succeeded)
        {
            var state = TenantPaymentAttemptState.Succeeded;
            await FinalizeInteractiveAttemptAsync(submit, provider, state, ct);
            return CheckoutResult.Succeeded(paymentAttemptId, ProviderObjectId(provider));
        }

        if (!provider.ConfirmedNoProviderObject
            && MapProviderState(provider.Status) is TenantPaymentAttemptState.Canceled
                or TenantPaymentAttemptState.Failed)
            return await AbandonConfirmedAttemptAsync(attempt, provider,
                MapProviderState(provider.Status), reason, ct);

        if (!provider.ConfirmedNoProviderObject)
        {
            provider = await _interactiveProvider.CancelOrExpireAsync(
                ToInteractiveAttempt(attempt), ct);
            if (provider is null)
                return CheckoutResult.Pending(paymentAttemptId, attempt.State.ToString(), attempt.ProviderPaymentId);
            if (!provider.ConfirmedNoProviderObject
                && MapProviderState(provider.Status) == TenantPaymentAttemptState.Succeeded)
            {
                await FinalizeInteractiveAttemptAsync(submit, provider,
                    TenantPaymentAttemptState.Succeeded, ct);
                return CheckoutResult.Succeeded(paymentAttemptId, ProviderObjectId(provider));
            }
            if (!provider.ConfirmedNoProviderObject
                && MapProviderState(provider.Status) is not (TenantPaymentAttemptState.Canceled
                    or TenantPaymentAttemptState.Failed))
                return CheckoutResult.Pending(paymentAttemptId, attempt.State.ToString(),
                    attempt.ProviderPaymentId);
        }

        var terminal = provider.ConfirmedNoProviderObject
            ? TenantPaymentAttemptState.Canceled
            : MapProviderState(provider.Status);
        return await AbandonConfirmedAttemptAsync(attempt, provider, terminal, reason, ct);
    }

    private async Task<CheckoutResult> AbandonConfirmedAttemptAsync(
        InspectProviderPaymentAttemptResult attempt, InteractiveProviderObject provider,
        TenantPaymentAttemptState state, string reason, CancellationToken ct)
    {
        if (state is not (TenantPaymentAttemptState.Canceled or TenantPaymentAttemptState.Failed))
            return CheckoutResult.Pending(attempt.PaymentAttemptId, attempt.State.ToString(),
                attempt.ProviderPaymentId);
        var command = new AbandonProviderPaymentAttemptCommand(
                attempt.PortfolioId, attempt.TenantAccountId, attempt.PaymentAttemptId,
                attempt.Provider, attempt.IdempotencyKey,
                string.IsNullOrWhiteSpace(reason) ? "Tenant canceled hosted payment." : reason,
                _timeProvider.UtcNow(), ProviderConfirmed: true,
                ConfirmedState: state,
                ProviderPaymentId: ProviderObjectId(provider));
        var abandoned = await _writes.ExecuteAsync(attempt.IdempotencyKey,
            ProviderPaymentWriteSupport.Write<AbandonProviderPaymentAttemptCommand,
                AbandonProviderPaymentAttemptResult>(_db, "payments.provider-attempt.abandon", command), ct);
        return abandoned.Value.State switch
        {
            TenantPaymentAttemptState.Canceled => CheckoutResult.Canceled(attempt.PaymentAttemptId),
            TenantPaymentAttemptState.Failed => CheckoutResult.Failed(attempt.PaymentAttemptId),
            TenantPaymentAttemptState.Succeeded => CheckoutResult.Succeeded(
                attempt.PaymentAttemptId, attempt.ProviderPaymentId),
            _ => CheckoutResult.Pending(attempt.PaymentAttemptId, abandoned.Value.State.ToString(),
                attempt.ProviderPaymentId),
        };
    }

    private async Task FinalizeInteractiveAttemptAsync(
        SubmitProviderPaymentCreateResult attempt, InteractiveProviderObject provider,
        TenantPaymentAttemptState state, CancellationToken ct)
    {
        var providerObjectId = ProviderObjectId(provider);
        if (string.IsNullOrWhiteSpace(providerObjectId))
            throw new InvalidOperationException(
                $"Provider reconciliation for payment attempt {attempt.PaymentAttemptId} did not return an object identity.");
        var operationName = $"payments.provider-create.finalize:{state}";
        var command = new FinalizeProviderPaymentCreateCommand(
            attempt.PortfolioId, attempt.TenantAccountId, attempt.PaymentAttemptId,
            attempt.Provider, attempt.IdempotencyKey, providerObjectId, state, null,
            _timeProvider.UtcNow(), attempt.ProviderFenceToken);
        await _writes.ExecuteAsync(attempt.IdempotencyKey,
            // The durable attempt key identifies the provider object, while the command type
            // identifies the state transition. A response-lost Submitted finalize may be
            // followed by a Succeeded reconciliation; those are two legitimate atomic commands,
            // not an idempotency conflict for one command payload.
            ProviderPaymentWriteSupport.Write<FinalizeProviderPaymentCreateCommand,
                FinalizeProviderPaymentCreateResult>(_db, operationName, command), ct);
    }

    private async Task FailInteractiveAttemptAsync(
        SubmitProviderPaymentCreateResult attempt, InteractiveProviderException exception,
        CancellationToken ct)
    {
        var command = new FailProviderPaymentCreateCommand(
            attempt.PortfolioId, attempt.TenantAccountId, attempt.PaymentAttemptId,
            attempt.Provider, attempt.IdempotencyKey, exception.FailureCode,
            exception.Message, _timeProvider.UtcNow(), attempt.ProviderFenceToken);
        await _writes.ExecuteAsync(attempt.IdempotencyKey,
            ProviderPaymentWriteSupport.Write<FailProviderPaymentCreateCommand,
                FailProviderPaymentCreateResult>(_db, "payments.provider-create.fail", command), ct);
    }

    private static string ProviderObjectId(InteractiveProviderObject provider) =>
        provider.ProviderPaymentId
        ?? provider.PaymentIntentId
        ?? provider.CheckoutSessionId
        ?? string.Empty;

    private static TenantPaymentAttemptState MapProviderState(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "succeeded" or "paid" or "complete" => TenantPaymentAttemptState.Succeeded,
            "canceled" or "cancelled" or "expired" => TenantPaymentAttemptState.Canceled,
            "failed" or "payment_failed" or "setup_failed" => TenantPaymentAttemptState.Failed,
            _ => TenantPaymentAttemptState.Submitted,
        };

    private static SubmitProviderPaymentCreateOutcome OutcomeForState(TenantPaymentAttemptState state) =>
        state switch
        {
            TenantPaymentAttemptState.Succeeded => SubmitProviderPaymentCreateOutcome.Succeeded,
            TenantPaymentAttemptState.Canceled => SubmitProviderPaymentCreateOutcome.Canceled,
            TenantPaymentAttemptState.Failed => SubmitProviderPaymentCreateOutcome.Failed,
            _ => SubmitProviderPaymentCreateOutcome.AlreadySubmitted,
        };

    private static SubmitProviderPaymentCreateResult ToSubmitResult(
        PrepareProviderPaymentCreateResult prepared) => new(
            prepared.State == TenantPaymentAttemptState.Prepared
                ? SubmitProviderPaymentCreateOutcome.Submitted
                : OutcomeForState(prepared.State),
            prepared.PortfolioId, prepared.TenantAccountId, prepared.PaymentAttemptId,
            prepared.State, prepared.Amount, prepared.Currency, prepared.Provider,
            prepared.IdempotencyKey, prepared.ProviderFenceToken, prepared.ProviderPaymentId,
            prepared.PreparedAtUtc);

    private static SubmitProviderPaymentCreateResult ToSubmitResult(
        PrepareProviderAutopaySetupResult prepared) => new(
            prepared.State == TenantPaymentAttemptState.Prepared
                ? SubmitProviderPaymentCreateOutcome.Submitted
                : OutcomeForState(prepared.State),
            prepared.PortfolioId, prepared.TenantAccountId, prepared.PaymentAttemptId,
            prepared.State, 0m, "USD", prepared.Provider, prepared.IdempotencyKey,
            prepared.ProviderFenceToken, prepared.ProviderPaymentId, null);

    private static SubmitProviderPaymentCreateResult ToSubmitResult(
        InspectProviderPaymentAttemptResult attempt) => new(
            OutcomeForState(attempt.State), attempt.PortfolioId, attempt.TenantAccountId,
            attempt.PaymentAttemptId, attempt.State, attempt.Amount, attempt.Currency,
            attempt.Provider, attempt.IdempotencyKey, attempt.ProviderFenceToken,
            attempt.ProviderPaymentId, attempt.PreparedAtUtc);

    private static InteractiveProviderAttempt ToInteractiveAttempt(
        PrepareProviderPaymentCreateResult attempt) => new(
            attempt.PaymentAttemptId, attempt.Provider, attempt.IdempotencyKey,
            TenantPaymentAttemptType.Charge, attempt.ProviderPaymentId,
            attempt.PortfolioId, attempt.TenantAccountId, attempt.Amount, attempt.Currency);

    private static InteractiveProviderAttempt ToInteractiveAttempt(
        PrepareProviderAutopaySetupResult attempt) => new(
            attempt.PaymentAttemptId, attempt.Provider, attempt.IdempotencyKey,
            TenantPaymentAttemptType.Verification, attempt.ProviderPaymentId,
            attempt.PortfolioId, attempt.TenantAccountId, 0m, "USD");

    private static InteractiveProviderAttempt ToInteractiveAttempt(
        SubmitProviderPaymentCreateResult attempt) => new(
            attempt.PaymentAttemptId, attempt.Provider, attempt.IdempotencyKey,
            attempt.Amount == 0m ? TenantPaymentAttemptType.Verification : TenantPaymentAttemptType.Charge,
            attempt.ProviderPaymentId, attempt.PortfolioId, attempt.TenantAccountId,
            attempt.Amount, attempt.Currency);

    private static InteractiveProviderAttempt ToInteractiveAttempt(
        InspectProviderPaymentAttemptResult attempt) => new(
            attempt.PaymentAttemptId, attempt.Provider, attempt.IdempotencyKey,
            attempt.AttemptType, attempt.ProviderPaymentId, attempt.PortfolioId,
            attempt.TenantAccountId, attempt.Amount, attempt.Currency);

    private Task<AtomicCommandOutcome<SubmitProviderPaymentCreateResult>> SubmitProviderAttemptAsync(
        PrepareProviderPaymentCreateResult prepared, int portfolioId, int tenantAccountId,
        CancellationToken ct) => SubmitProviderAttemptAsync(
            portfolioId, tenantAccountId, prepared.PaymentAttemptId, prepared.Provider,
            prepared.IdempotencyKey, ct);

    private Task<AtomicCommandOutcome<SubmitProviderPaymentCreateResult>> SubmitProviderAttemptAsync(
        PrepareProviderAutopaySetupResult prepared, int portfolioId, int tenantAccountId,
        CancellationToken ct) => SubmitProviderAttemptAsync(
            portfolioId, tenantAccountId, prepared.PaymentAttemptId, prepared.Provider,
            prepared.IdempotencyKey, ct);

    private Task<AtomicCommandOutcome<SubmitProviderPaymentCreateResult>> SubmitProviderAttemptAsync(
        int portfolioId, int tenantAccountId, long paymentAttemptId, string provider,
        string idempotencyKey, CancellationToken ct)
    {
        var command = new SubmitProviderPaymentCreateCommand(
            portfolioId, tenantAccountId, paymentAttemptId, provider, idempotencyKey,
            _timeProvider.UtcNow());
        return _writes.ExecuteAsync(idempotencyKey,
            ProviderPaymentWriteSupport.Write<SubmitProviderPaymentCreateCommand,
                SubmitProviderPaymentCreateResult>(_db, "payments.provider-create.submit", command), ct);
    }

    /// <summary>
    /// Resolves the success URL: an explicit request value is honored ONLY when it passes the
    /// return-URL allowlist (so a client can't turn Checkout into an open redirect to a phishing
    /// site); otherwise the trusted configured value, else a built-in.
    /// </summary>
    private string ResolveSuccessUrl(string? requested, long paymentAttemptId, long? chargeLedgerEntryId) =>
        AppendAttemptIdentity(
            IsAllowedReturnUrl(requested) ? requested!
            : !string.IsNullOrWhiteSpace(_config.CheckoutSuccessUrl) ? _config.CheckoutSuccessUrl!
            : DefaultSuccessUrl,
            paymentAttemptId, chargeLedgerEntryId);

    /// <summary>Resolves the cancel URL with the same allowlist guard as the success URL.</summary>
    private string ResolveCancelUrl(string? requested, long paymentAttemptId, long? chargeLedgerEntryId) =>
        AppendAttemptIdentity(
            IsAllowedReturnUrl(requested) ? requested!
            : !string.IsNullOrWhiteSpace(_config.CheckoutCancelUrl) ? _config.CheckoutCancelUrl!
            : DefaultCancelUrl,
            paymentAttemptId, chargeLedgerEntryId);

    private static string AppendAttemptIdentity(
        string url, long paymentAttemptId, long? chargeLedgerEntryId)
    {
        var fragmentIndex = url.IndexOf('#');
        var beforeFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var separator = beforeFragment.Contains('?')
            ? beforeFragment.EndsWith('?') || beforeFragment.EndsWith('&') ? string.Empty : "&"
            : "?";
        var identity = $"paymentAttemptId={Uri.EscapeDataString(paymentAttemptId.ToString())}";
        if (chargeLedgerEntryId is long entryId)
            identity += $"&chargeLedgerEntryId={Uri.EscapeDataString(entryId.ToString())}";
        return $"{beforeFragment}{separator}{identity}{fragment}";
    }

    /// <summary>
    /// A client-supplied return URL is trusted only if it is an absolute URL whose host is
    /// explicitly allowed: either it appears in <see cref="StripeConfig.AllowedReturnHosts"/>
    /// (https only), or it is a loopback host (localhost/127.0.0.1/::1) for local development.
    /// Anything else (other hosts, non-http schemes, relative/garbage) is rejected so we fall back
    /// to the server-configured URL — closing the open-redirect vector.
    /// </summary>
    private bool IsAllowedReturnUrl(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            !Uri.TryCreate(requested, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.IsLoopback &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true; // local dev (localhost / 127.0.0.1 / ::1)
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && _config.AllowedReturnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the idempotency identity for one caller-owned provider attempt. The actor is part of the
    /// server key so two authorized payers can prepare the same charge independently; the nonce is
    /// retained across retries so two identical taps still share one atomic receipt and Stripe key.
    /// </summary>
    internal static string BuildPaymentIdempotencyKey(
        string kind, long chargeLedgerEntryId, int actorUserId, string? attemptKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chargeLedgerEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        var nonce = attemptKey?.Trim();
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128)
            throw new ArgumentException("A payment attempt key of 1 to 128 characters is required.", nameof(attemptKey));

        var key = $"{kind}:tenant-charge:{chargeLedgerEntryId}:actor:{actorUserId}:attempt:{nonce}";
        if (key.Length > 200)
            throw new ArgumentException("The payment attempt key is too long.", nameof(attemptKey));
        return key;
    }
}
