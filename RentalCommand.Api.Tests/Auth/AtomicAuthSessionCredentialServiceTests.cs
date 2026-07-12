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

    private static AtomicAuthSessionCredentialService CreateService(
        IAtomicUnitOfWork atomic,
        RefreshCredentialTokenFactory tokens) =>
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
            TimeProvider.System);

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
        public int CallCount { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            IAtomicResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            CallCount++;
            LastCommand = command;
            var value = (TResult)_resultFactory(command, typeof(TResult));
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                value,
                _disposition,
                Guid.NewGuid()));
        }
    }
}
