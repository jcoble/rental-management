using FluentAssertions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

public sealed class AtomicAuthSessionCredentialServiceTests
{
    private static readonly string SigningKey = Convert.ToBase64String(
        Enumerable.Range(1, RefreshCredentialTokenFactory.MinimumSigningKeyBytes)
            .Select(value => (byte)value)
            .ToArray());

    [Fact]
    public async Task StartAsync_ReconstructsReceiptCredentialWithoutPersistingBearer()
    {
        var receiptCredentialId = Guid.NewGuid();
        var atomic = new CapturingAtomicUnitOfWork((_, resultType) =>
        {
            resultType.Should().Be(typeof(StartAuthSessionResult));
            return new StartAuthSessionResult(
                true,
                Guid.NewGuid(),
                41,
                72,
                9,
                3,
                Guid.NewGuid(),
                receiptCredentialId);
        }, AtomicCommandDisposition.Replayed);
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var service = CreateService(atomic, tokens);
        var challengeBearer = "one-time-context-selection-secret";

        var result = await service.StartAsync(new AtomicAuthSessionStartRequest(
            Guid.NewGuid(),
            41,
            72,
            3,
            Guid.NewGuid(),
            challengeBearer));

        result.Started.Should().BeTrue();
        result.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        result.RefreshBearer.Should().NotBeNull();
        tokens.TryValidateAndReadCredentialId(result.RefreshBearer!, out var reconstructedId)
            .Should().BeTrue();
        reconstructedId.Should().Be(receiptCredentialId);

        var command = atomic.LastCommand.Should().BeOfType<StartAuthSessionCommand>().Subject;
        command.CredentialTokenHash.Should().Be(
            tokens.HashBearer(tokens.CreateBearer(command.CredentialId)));
        command.ContextSelectionChallengeTokenHash.Should().Be(tokens.HashBearer(challengeBearer));
        command.CredentialTokenHash.Should().NotContain(RefreshCredentialTokenFactory.Prefix);
        command.ContextSelectionChallengeTokenHash.Should().NotContain(challengeBearer);
    }

    [Fact]
    public async Task StartAsync_SameOperationProducesReplayCompatibleCommand()
    {
        var atomic = new CapturingAtomicUnitOfWork((command, resultType) =>
        {
            resultType.Should().Be(typeof(StartAuthSessionResult));
            var start = command.Should().BeOfType<StartAuthSessionCommand>().Subject;
            return new StartAuthSessionResult(
                true,
                start.AuthSessionId,
                start.UserId,
                start.SelectedAccessContextId,
                9,
                start.ExpectedAccessRevision,
                start.RefreshTokenFamilyId,
                start.CredentialId);
        }, AtomicCommandDisposition.Executed);
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var service = CreateService(atomic, tokens, new AdvancingAuthSecurityClock());
        var request = new AtomicAuthSessionStartRequest(
            Guid.NewGuid(),
            41,
            72,
            3,
            Guid.NewGuid(),
            "one-time-context-selection-secret");

        var firstResult = await service.StartAsync(request);
        var secondResult = await service.StartAsync(request);

        atomic.Commands.Should().HaveCount(2);
        var first = atomic.Commands[0].Should().BeOfType<StartAuthSessionCommand>().Subject;
        var second = atomic.Commands[1].Should().BeOfType<StartAuthSessionCommand>().Subject;
        first.IssuedAtUtc.Should().NotBe(second.IssuedAtUtc);
        first.AuthSessionId.Should().Be(second.AuthSessionId);
        first.RefreshTokenFamilyId.Should().Be(second.RefreshTokenFamilyId);
        first.CredentialId.Should().Be(second.CredentialId);
        first.CredentialTokenHash.Should().Be(second.CredentialTokenHash);
        AtomicCommandFingerprint.Create(first).Should().Be(AtomicCommandFingerprint.Create(second));
        firstResult.RefreshBearer.Should().Be(secondResult.RefreshBearer);
    }

    [Fact]
    public async Task RotateAsync_RecoveredReceiptReconstructsOriginalReplacementBearer()
    {
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var presentedBearer = tokens.CreateBearer(Guid.NewGuid());
        var receiptReplacementId = Guid.NewGuid();
        var atomic = new CapturingAtomicUnitOfWork((_, resultType) =>
        {
            resultType.Should().Be(typeof(SessionRefreshMutationResult));
            return new SessionRefreshMutationResult(
                SessionRefreshMutationStatus.Recovered,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                receiptReplacementId);
        }, AtomicCommandDisposition.Replayed);
        var service = CreateService(atomic, tokens);

        var result = await service.RotateAsync(new AtomicAuthSessionRotationRequest(
            Guid.NewGuid(),
            presentedBearer));

        result.Status.Should().Be(SessionRefreshMutationStatus.Recovered);
        result.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        result.ReplacementBearer.Should().NotBeNull();
        tokens.TryValidateAndReadCredentialId(result.ReplacementBearer!, out var reconstructedId)
            .Should().BeTrue();
        reconstructedId.Should().Be(receiptReplacementId);

        var command = atomic.LastCommand.Should().BeOfType<RotateSessionRefreshCredentialCommand>().Subject;
        command.PresentedTokenHash.Should().Be(tokens.HashBearer(presentedBearer));
        command.ReplacementTokenHash.Should().Be(
            tokens.HashBearer(tokens.CreateBearer(command.ReplacementCredentialId)));
        command.PresentedTokenHash.Should().NotContain(presentedBearer);
    }

    [Fact]
    public async Task RotateAsync_RejectsInvalidBearerBeforeAtomicPersistence()
    {
        var atomic = new CapturingAtomicUnitOfWork(
            (_, _) => throw new InvalidOperationException("Persistence must not be called."),
            AtomicCommandDisposition.Executed);
        var service = CreateService(atomic, new RefreshCredentialTokenFactory(SigningKey));

        var result = await service.RotateAsync(new AtomicAuthSessionRotationRequest(
            Guid.NewGuid(),
            "not-a-signed-refresh-bearer"));

        result.Status.Should().Be(SessionRefreshMutationStatus.Rejected);
        result.Disposition.Should().BeNull();
        result.ReplacementBearer.Should().BeNull();
        atomic.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RotateAsync_UsesTheRequestOperationAsTheRotationReceiptIdentity()
    {
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var operationId = Guid.NewGuid();
        var presentedBearer = tokens.CreateBearer(Guid.NewGuid());
        var atomic = new CapturingAtomicUnitOfWork((command, resultType) =>
        {
            resultType.Should().Be(typeof(SessionRefreshMutationResult));
            var rotation = command.Should().BeOfType<RotateSessionRefreshCredentialCommand>().Subject;
            return new SessionRefreshMutationResult(
                SessionRefreshMutationStatus.Rotated,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                rotation.ReplacementCredentialId);
        }, AtomicCommandDisposition.Executed);
        var service = CreateService(atomic, tokens);

        await service.RotateAsync(new AtomicAuthSessionRotationRequest(operationId, presentedBearer));

        var identity = atomic.Identities.Single();
        identity.CommandType.Should().Be("session-refresh:rotate");
        identity.IdempotencyKey.Should().Be($"operation:{operationId:N}");
        atomic.LastCommand.Should().BeOfType<RotateSessionRefreshCredentialCommand>().Subject
            .OperationId.Should().Be(operationId);
    }

    private static AtomicAuthSessionCredentialService CreateService(
        IAtomicUnitOfWork atomic,
        RefreshCredentialTokenFactory tokens,
        IAuthSecurityClock? securityClock = null) =>
        new(
            atomic,
            tokens,
            Options.Create(new AtomicAuthSessionCredentialOptions
            {
                SigningKey = SigningKey,
                CredentialLifetimeDays = 7,
                FamilyAbsoluteLifetimeDays = 30,
                SessionLifetimeDays = 30,
            }),
            securityClock ?? new SystemAuthSecurityClock());

    private sealed class AdvancingAuthSecurityClock : IAuthSecurityClock
    {
        private DateTime _utcNow = new(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow()
        {
            var current = _utcNow;
            _utcNow = _utcNow.AddSeconds(1);
            return current;
        }
    }

    private sealed class CapturingAtomicUnitOfWork : IAtomicUnitOfWork
    {
        private readonly Func<object, Type, object> _resultFactory;
        private readonly AtomicCommandDisposition _disposition;

        public CapturingAtomicUnitOfWork(
            Func<object, Type, object> resultFactory,
            AtomicCommandDisposition disposition)
        {
            _resultFactory = resultFactory;
            _disposition = disposition;
        }

        public object? LastCommand { get; private set; }
        public List<object> Commands { get; } = [];
        public List<AtomicCommandIdentity> Identities { get; } = [];
        public int CallCount { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            AtomicJsonResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            CallCount++;
            LastCommand = command;
            Commands.Add(command);
            Identities.Add(identity);
            var value = (TResult)_resultFactory(command, typeof(TResult));
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                value,
                _disposition,
                Guid.NewGuid()));
        }
    }
}
