using FluentAssertions;
using RentalCommand.Core.Auth;

namespace RentalCommand.Core.Tests.Auth;

public sealed class RefreshCredentialTokenFactoryTests
{
    private static readonly string SigningKey = Convert.ToBase64String(
        Enumerable.Range(1, RefreshCredentialTokenFactory.MinimumSigningKeyBytes)
            .Select(value => (byte)value)
            .ToArray());

    [Fact]
    public void CreateBearer_IsStableAndRoundTripsCredentialId()
    {
        var factory = new RefreshCredentialTokenFactory(SigningKey);
        var credentialId = Guid.NewGuid();

        var first = factory.CreateBearer(credentialId);
        var replay = factory.CreateBearer(credentialId);

        replay.Should().Be(first);
        factory.TryValidateAndReadCredentialId(first, out var parsed).Should().BeTrue();
        parsed.Should().Be(credentialId);
        factory.HashBearer(replay).Should().Be(factory.HashBearer(first));
    }

    [Fact]
    public void CreateBearer_ProducesDifferentCredentialForDifferentId()
    {
        var factory = new RefreshCredentialTokenFactory(SigningKey);

        factory.CreateBearer(Guid.NewGuid()).Should().NotBe(factory.CreateBearer(Guid.NewGuid()));
    }

    [Fact]
    public void TryValidateAndReadCredentialId_RejectsTamperedBearer()
    {
        var factory = new RefreshCredentialTokenFactory(SigningKey);
        var bearer = factory.CreateBearer(Guid.NewGuid());
        var replacement = bearer[^1] == 'A' ? 'B' : 'A';
        var tampered = bearer[..^1] + replacement;

        factory.TryValidateAndReadCredentialId(tampered, out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_RejectsSigningKeyShorterThan256Bits()
    {
        var weakKey = Convert.ToBase64String(new byte[RefreshCredentialTokenFactory.MinimumSigningKeyBytes - 1]);

        var act = () => new RefreshCredentialTokenFactory(weakKey);

        act.Should().Throw<ArgumentException>();
    }
}
