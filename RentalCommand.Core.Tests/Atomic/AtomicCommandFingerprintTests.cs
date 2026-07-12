using FluentAssertions;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Tests.Atomic;

public sealed class AtomicCommandFingerprintTests
{
    [Fact]
    public void Create_SortsObjectPropertiesAndPreservesBusinessPayload()
    {
        var first = new FingerprintCommand(
            PortfolioId: 12,
            Amount: 25.50m,
            Details: new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" },
            AuthSessionId: Guid.NewGuid(),
            ExpectedAccessRevision: 3,
            PreparedAtUtc: new DateTime(2026, 7, 12, 1, 0, 0, DateTimeKind.Utc),
            RequestedAtUtc: new DateTime(2026, 7, 12, 1, 0, 0, DateTimeKind.Utc),
            DeliveryIdempotencyKey: "delivery-one");
        var retry = first with
        {
            Details = new Dictionary<string, string> { ["a"] = "first", ["z"] = "last" },
            AuthSessionId = Guid.NewGuid(),
            ExpectedAccessRevision = 4,
            PreparedAtUtc = first.PreparedAtUtc.AddMinutes(2),
            RequestedAtUtc = first.RequestedAtUtc.AddMinutes(2),
            DeliveryIdempotencyKey = "delivery-two",
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(first));
    }

    [Fact]
    public void Create_WhenBusinessPayloadChanges_ReturnsDifferentFingerprint()
    {
        var command = new FingerprintCommand(
            PortfolioId: 12,
            Amount: 25.50m,
            Details: new Dictionary<string, string> { ["a"] = "first" },
            AuthSessionId: Guid.NewGuid(),
            ExpectedAccessRevision: 3,
            PreparedAtUtc: DateTime.UtcNow,
            RequestedAtUtc: DateTime.UtcNow,
            DeliveryIdempotencyKey: "delivery");

        AtomicCommandFingerprint.Create(command with { Amount = 26.50m })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    private sealed record FingerprintCommand(
        int PortfolioId,
        decimal Amount,
        IReadOnlyDictionary<string, string> Details,
        Guid AuthSessionId,
        long ExpectedAccessRevision,
        DateTime PreparedAtUtc,
        DateTime RequestedAtUtc,
        string DeliveryIdempotencyKey) : IAtomicCommandData;
}
