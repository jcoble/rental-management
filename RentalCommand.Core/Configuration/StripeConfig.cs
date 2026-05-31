namespace RentalCommand.Core.Configuration;

public class StripeConfig
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; set; }
    public string? PublishableKey { get; set; }
    public string? WebhookSecret { get; set; }

    /// <summary>True when a Stripe secret key has been configured. Gating flag — when false, all Stripe features are disabled.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);
}
