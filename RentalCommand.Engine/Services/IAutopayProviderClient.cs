using Stripe;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Deterministic provider seam for autopay. Reconciliation is always attempted before a new
/// provider create for a durable unresolved attempt, which makes an accepted-but-unfinalized call
/// safe across worker crashes and provider idempotency-window expiry.
/// </summary>
public interface IAutopayProviderClient
{
    Task<AutopayProviderPayment> CreateAsync(
        TenantPaymentAttempt attempt,
        string customerId,
        string paymentMethodId,
        CancellationToken ct);

    Task<AutopayProviderPayment?> ReconcileAsync(
        TenantPaymentAttempt attempt,
        CancellationToken ct);
}

public sealed record AutopayProviderPayment(
    string ProviderPaymentId,
    string Status,
    string IdempotencyKey);

public sealed class StripeAutopayProviderClient : IAutopayProviderClient
{
    private readonly StripeConfig _config;

    public StripeAutopayProviderClient(Microsoft.Extensions.Options.IOptions<StripeConfig> config) =>
        _config = config.Value;

    public async Task<AutopayProviderPayment> CreateAsync(
        TenantPaymentAttempt attempt,
        string customerId,
        string paymentMethodId,
        CancellationToken ct)
    {
        var intent = await new PaymentIntentService().CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)(attempt.Amount * 100m),
            Currency = attempt.Currency.ToLowerInvariant(),
            Customer = customerId,
            PaymentMethod = paymentMethodId,
            Confirm = true,
            OffSession = true,
            Metadata = new Dictionary<string, string>
            {
                ["portfolioId"] = attempt.PortfolioId.ToString(),
                ["tenantAccountId"] = attempt.TenantAccountId.ToString(),
                ["chargeLedgerEntryId"] = attempt.ChargeLedgerEntryId?.ToString() ?? string.Empty,
                ["paymentAttemptId"] = attempt.Id.ToString(),
                ["autopay"] = "1",
            },
        }, new RequestOptions
        {
            ApiKey = _config.SecretKey,
            IdempotencyKey = attempt.IdempotencyKey,
        }, ct);

        return new(intent.Id, intent.Status, attempt.IdempotencyKey);
    }

    public async Task<AutopayProviderPayment?> ReconcileAsync(
        TenantPaymentAttempt attempt, CancellationToken ct)
    {
        // Stripe Search is metadata-based, so it remains useful after the provider idempotency
        // window expires and does not create another charge.
        var matches = await new PaymentIntentService().SearchAsync(
            new PaymentIntentSearchOptions
            {
                Query = $"metadata['paymentAttemptId']:'{attempt.Id}'",
                Limit = 10,
            }, new RequestOptions { ApiKey = _config.SecretKey }, ct);
        var match = matches.Data.FirstOrDefault();
        return match is null ? null : new(match.Id, match.Status, attempt.IdempotencyKey);
    }
}
