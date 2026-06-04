using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Security;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Verifies the e-sign webhook signature check: enforcement only when a secret is configured, a correct
/// HMAC-SHA256(secret, event_time + event_type) passes, and a forged/missing hash is rejected.
/// </summary>
public sealed class EsignWebhookSignatureValidatorTests
{
    private const string Secret = "whsec_test_secret";
    private const string EventTime = "1718000000";
    private const string EventType = "signature_request_all_signed";

    [Fact]
    public void NotEnforced_WhenNoSecretConfigured()
    {
        var validator = Create(webhookSecret: null);

        validator.IsEnforced.Should().BeFalse();
        // When not enforced, the method is total and returns true (the controller decides policy).
        validator.IsValid(EventTime, EventType, providedHash: null).Should().BeTrue();
    }

    [Fact]
    public void ValidHash_Passes()
    {
        var validator = Create(Secret);
        var hash = Hmac(Secret, EventTime + EventType);

        validator.IsEnforced.Should().BeTrue();
        validator.IsValid(EventTime, EventType, hash).Should().BeTrue();
    }

    [Fact]
    public void ForgedHash_Rejected()
    {
        var validator = Create(Secret);

        validator.IsValid(EventTime, EventType, providedHash: "deadbeef").Should().BeFalse();
    }

    [Fact]
    public void MissingHash_Rejected_WhenEnforced()
    {
        var validator = Create(Secret);

        validator.IsValid(EventTime, EventType, providedHash: null).Should().BeFalse();
    }

    private static EsignWebhookSignatureValidator Create(string? webhookSecret)
    {
        var config = Options.Create(new EsignConfig
        {
            ApiKey = "key_present",
            WebhookSecret = webhookSecret,
        });
        return new EsignWebhookSignatureValidator(config, NullLogger<EsignWebhookSignatureValidator>.Instance);
    }

    private static string Hmac(string secret, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
