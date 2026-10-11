using FluentAssertions;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Enums;

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

    [Fact]
    public void Create_HonorsIgnoreDeclaredByCommandInterface()
    {
        var command = new InterfaceFingerprintCommand(
            Amount: 25.50m,
            AuthSessionId: Guid.NewGuid(),
            RequestedAtUtc: new DateTime(2026, 7, 12, 1, 0, 0, DateTimeKind.Utc));

        AtomicCommandFingerprint.Create(command with { AuthSessionId = Guid.NewGuid() })
            .Should().Be(AtomicCommandFingerprint.Create(command));
    }

    [Fact]
    public void Create_DoesNotInferIgnoreFromBusinessPropertyName()
    {
        var command = new InterfaceFingerprintCommand(
            Amount: 25.50m,
            AuthSessionId: Guid.NewGuid(),
            RequestedAtUtc: new DateTime(2026, 7, 12, 1, 0, 0, DateTimeKind.Utc));

        AtomicCommandFingerprint.Create(command with { RequestedAtUtc = command.RequestedAtUtc.AddMinutes(1) })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    [Fact]
    public void Create_PlaidRetryIgnoresRandomizedCiphertextButPreservesStableHashes()
    {
        var command = new PreparePlaidTokenExchangeCommand(
            PortfolioId: 12,
            ActorUserId: 44,
            AuthSessionId: Guid.NewGuid(),
            AccessContextId: 8,
            ExpectedAccessRevision: 1,
            RequiredCapability: RentalCommand.Core.Authorization.CapabilityKeys.BankConnectionsManage,
            ClientOperationId: "operation-1",
            RequestHash: "request-hash",
            PublicTokenHash: "public-token-hash",
            InstitutionName: "Rental Bank",
            AccountName: "Checking",
            AccountMask: "1234",
            AccountType: "depository",
            AccountSubtype: "checking",
            ExternalAccountIdCipherText: "ciphertext-one",
            ExternalAccountIdHash: "external-account-hash",
            PreparedAtUtc: new DateTime(2026, 7, 12, 1, 0, 0, DateTimeKind.Utc));
        var retry = command with
        {
            AuthSessionId = Guid.NewGuid(),
            AccessContextId = 9,
            ExpectedAccessRevision = 2,
            ExternalAccountIdCipherText = "ciphertext-two",
            PreparedAtUtc = command.PreparedAtUtc.AddMinutes(2),
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { RequestHash = "changed-request-hash" })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ExternalAccountIdHash = "changed-account-hash" })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ActorUserId = 45 })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with
            {
                RequiredCapability = RentalCommand.Core.Authorization.CapabilityKeys.IntegrationsManage,
            })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    [Fact]
    public void Create_AccountingMappingRetriesIgnoreAccessEnvelopeButPreserveActorAndCapability()
    {
        var command = new ConfirmAccountingMappingCommand(
            PortfolioId: 12,
            AccountingConnectionId: 8,
            Provider: AccountingProvider.QuickBooks,
            ConfirmedByUserId: 44,
            AuthSessionId: Guid.NewGuid(),
            AccessContextId: 3,
            ExpectedAccessRevision: 1,
            RequiredCapability: CapabilityKeys.IntegrationsManage,
            ExternalType: "Customer",
            ExternalId: "external-1",
            ExternalDisplayName: "Tenant",
            LocalEntityType: "Tenant",
            LocalEntityId: 9,
            LocalEnumValue: null,
            ClientOperationId: "mapping-operation",
            ExpectedRevision: 0,
            ConfirmedAtUtc: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        var retry = command with
        {
            AuthSessionId = Guid.NewGuid(),
            AccessContextId = 4,
            ExpectedAccessRevision = 2,
            ConfirmedAtUtc = command.ConfirmedAtUtc.AddMinutes(2),
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ConfirmedByUserId = 45 })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { RequiredCapability = CapabilityKeys.BankConnectionsManage })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));

        var continuation = new ContinueAccountingMappingPromotionCommand(
            PortfolioId: 12,
            AccountingConnectionId: 8,
            ContinuationId: Guid.NewGuid(),
            RequestedByUserId: 44,
            AuthSessionId: Guid.NewGuid(),
            AccessContextId: 3,
            ExpectedAccessRevision: 1,
            RequiredCapability: CapabilityKeys.IntegrationsManage,
            ClientOperationId: "continuation-operation",
            AppliedAtUtc: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        AtomicCommandFingerprint.Create(continuation with
            {
                AuthSessionId = Guid.NewGuid(),
                AccessContextId = 4,
                ExpectedAccessRevision = 2,
                AppliedAtUtc = continuation.AppliedAtUtc.AddMinutes(2),
            })
            .Should().Be(AtomicCommandFingerprint.Create(continuation));
    }

    [Fact]
    public void Create_AiCredentialRetryIgnoresCiphertextAndAccessEnvelopeButPreservesSecretIntent()
    {
        var command = new ActivateWorkspaceLlmCredentialCommand(
            PortfolioId: 12,
            ActorUserId: 44,
            ActorAuthSessionId: Guid.NewGuid(),
            ActorAccessContextId: 8,
            ActorAccessRevision: 1,
            Provider: "openai",
            ModelId: "gpt-4o",
            ApiKeyIntentDigest: new string('a', 64),
            ApiKeyCipherText: "ciphertext-one",
            TestedAtUtc: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        var retry = command with
        {
            ActorAuthSessionId = Guid.NewGuid(),
            ActorAccessContextId = 9,
            ActorAccessRevision = 2,
            ApiKeyCipherText = "ciphertext-two",
            TestedAtUtc = command.TestedAtUtc.AddMinutes(5),
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ApiKeyIntentDigest = new string('b', 64) })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ActorUserId = 45 })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    [Fact]
    public void Create_PortfolioQaDeliveryRetryIgnoresAccessEnvelopeButPreservesActorAndAnswer()
    {
        var command = new PortfolioQaDeliveryCommand(
            PortfolioId: 12,
            ActorUserId: 44,
            ActorAuthSessionId: Guid.NewGuid(),
            ActorAccessContextId: 8,
            ActorAccessRevision: 1,
            Question: "Who is overdue?",
            Answer: "No tenants are overdue.",
            ToEmail: "landlord@example.test",
            ToSms: null,
            EmailDeliveryIdempotencyKey: "portfolio-qa:12:op:email",
            SmsDeliveryIdempotencyKey: "portfolio-qa:12:op:sms",
            CreatedAtUtc: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        var retry = command with
        {
            ActorAuthSessionId = Guid.NewGuid(),
            ActorAccessContextId = 9,
            ActorAccessRevision = 2,
            CreatedAtUtc = command.CreatedAtUtc.AddMinutes(5),
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { Answer = "Unit 2 is overdue." })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { ActorUserId = 45 })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    [Fact]
    public void Create_LlmUsageRetryIgnoresOccurrenceTimeButPreservesStableEventPayload()
    {
        var command = new RecordLlmUsageEvidenceCommand(
            PortfolioId: 12,
            UsageEventIdentity: "scan:first:attempt:1",
            Provider: "openai",
            ModelId: "gpt-4o",
            Feature: "scan.extraction",
            LatencyMilliseconds: 420,
            InputUnits: 120,
            OutputUnits: 30,
            EstimatedCostUsd: 0.00021m,
            OccurredAtUtc: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        var retry = command with
        {
            OccurredAtUtc = command.OccurredAtUtc.AddMinutes(5),
        };

        AtomicCommandFingerprint.Create(retry)
            .Should().Be(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { UsageEventIdentity = "scan:first:attempt:2" })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
        AtomicCommandFingerprint.Create(command with { InputUnits = 121 })
            .Should().NotBe(AtomicCommandFingerprint.Create(command));
    }

    private sealed record FingerprintCommand(
        int PortfolioId,
        decimal Amount,
        IReadOnlyDictionary<string, string> Details,
        [property: AtomicFingerprintIgnore]
        Guid AuthSessionId,
        [property: AtomicFingerprintIgnore]
        long ExpectedAccessRevision,
        [property: AtomicFingerprintIgnore]
        DateTime PreparedAtUtc,
        [property: AtomicFingerprintIgnore]
        DateTime RequestedAtUtc,
        [property: AtomicFingerprintIgnore]
        string DeliveryIdempotencyKey) : IAtomicCommandData;

    private interface IRetryEnvelope : IAtomicCommandData
    {
        [AtomicFingerprintIgnore]
        Guid AuthSessionId { get; }
    }

    private sealed record InterfaceFingerprintCommand(
        decimal Amount,
        Guid AuthSessionId,
        DateTime RequestedAtUtc) : IRetryEnvelope;
}
