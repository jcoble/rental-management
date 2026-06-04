namespace RentalCommand.Core.Configuration;

public class StripeConfig
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; set; }
    public string? PublishableKey { get; set; }
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Default URL Stripe Checkout returns the tenant to on success. The literal token
    /// <c>{CHECKOUT_SESSION_ID}</c> is preserved for Stripe to substitute. May be overridden per
    /// request. Used by both the rent-payment Checkout and the autopay setup Checkout.
    /// </summary>
    public string? CheckoutSuccessUrl { get; set; }

    /// <summary>Default URL Stripe Checkout returns the tenant to on cancel. May be overridden per request.</summary>
    public string? CheckoutCancelUrl { get; set; }

    /// <summary>True when a Stripe secret key has been configured. Gating flag — when false, all Stripe features are disabled.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);
}
