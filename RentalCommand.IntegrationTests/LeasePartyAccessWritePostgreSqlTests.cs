using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.IntegrationTests;

public sealed class LeasePartyAccessWritePostgreSqlTests
{
    private static readonly Guid SessionId =
        Guid.Parse("16e9cd8d-4f2b-43fa-9707-64da4ddd85f4");

    [Fact]
    public void Commands_PreserveFrozenLegacyFingerprints()
    {
        var commands = Commands();

        commands.Select(AtomicCommandFingerprint.Create).Should().Equal(
            "15066c13cef12eedbe13cc746f37e4e62afe43c91292c11038d2af1865a0b864",
            "7d6a8a3517cfab440013839a598b1b4cb75de2957d893b335fe05df14669a4db",
            "bdd90b896b3a7a41615dae4dc455b7b042f913ba5de5966da6972f8db8346494",
            "0b2d990d5c070fd7172740378b2840df7202712be77b1c911e520be4950d2e30",
            "89fdadadebbc7101057bcc345ebaeb95cc2704a9cda5ac05641ed7989cfaee51");
    }

    [Fact]
    public void WriteDescriptors_PreserveIdentitiesKeysCodecsAndLockProtocols()
    {
        var expected = new[]
        {
            ("lease-management.party.add", "lease-management.party.add.v1",
                "17:42:digest", WriteLockProtocol.LeaseParty, Array.Empty<string>()),
            ("lease-management.party.end", "lease-management.party.end.v1",
                "17:42:43:digest", WriteLockProtocol.LeaseParty, Array.Empty<string>()),
            ("lease-management.party.change-role", "lease-management.party.change-role.v1",
                "17:42:43:digest", WriteLockProtocol.LeaseParty, Array.Empty<string>()),
            ("lease-management.party.access.grant", "lease-management.party.access.grant.v1",
                "17:42:43:digest", WriteLockProtocol.LeasePartyAccessGrant,
                new[] { "TenantIdentityEmail", "WorkspaceAccessContext" }),
            ("lease-management.party.access.revoke", "lease-management.party.access.revoke.v1",
                "17:42:43:47:digest", WriteLockProtocol.LeasePartyAccessRevoke,
                new[] { "WorkspaceAccessContext" }),
        };

        Commands().Zip(expected).Should().AllSatisfy(pair =>
        {
            var write = Descriptor(pair.First);
            write.OperationName.Should().Be(pair.Second.Item1);
            write.ResultContract.Should().Be(pair.Second.Item2);
            LeasePartyAccessWriteSupport.IdempotencyKey(pair.First, "digest")
                .Should().Be(pair.Second.Item3);
            write.LockPlan.Protocol.Should().Be(pair.Second.Item4);
            write.LockPlan.Locks.Select(item => item.LockNamespace).Should()
                .Equal("LeaseManagement");
            write.LockPlan.DeferredLockNamespaces.Should().Equal(pair.Second.Item5);
        });
    }

    [Fact]
    public async Task LegacyHandlerEntryPoints_AreRetired()
    {
        var commands = Commands();
        var calls = new Func<Task>[]
        {
            () => new AddEffectivePartyHandler().HandleAsync(
                (AddEffectivePartyCommand)commands[0], null!, default),
            () => new EndEffectivePartyHandler().HandleAsync(
                (EndEffectivePartyCommand)commands[1], null!, default),
            () => new ChangeEffectivePartyRoleHandler().HandleAsync(
                (ChangeEffectivePartyRoleCommand)commands[2], null!, default),
            () => new GrantTenantUserAccessHandler().HandleAsync(
                (GrantTenantUserAccessCommand)commands[3], null!, default),
            () => new RevokeTenantUserAccessHandler().HandleAsync(
                (RevokeTenantUserAccessCommand)commands[4], null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*no longer use the legacy atomic handlers*");
        }
    }

    private static TransactionalWrite<ILeasePartyAccessCommand, LeasePartyMutationResult> Descriptor(
        ILeasePartyAccessCommand command) => LeasePartyAccessWriteSupport.Write(
        command,
        static (_, _, _) => Task.FromResult(new LeasePartyMutationResult(
            LeasePartyMutationOutcome.Applied, 42, 43, null, null, [], null)),
        static (_, _, _) => Task.CompletedTask);

    private static ILeasePartyAccessCommand[] Commands() =>
    [
        new AddEffectivePartyCommand(
            17, 42, 41, LeaseManagementPartyRole.Occupant, new DateOnly(2026, 8, 20),
            false, "Add household member", false, null, null, 5,
            SessionId, 12, 3, "ignored-add-delivery"),
        new EndEffectivePartyCommand(
            17, 42, 43, new DateOnly(2026, 8, 19), TenantAccessDisposition.RevokeImmediately,
            null, false, null, null, "End household member", 5,
            SessionId, 12, 3, "ignored-end-delivery"),
        new ChangeEffectivePartyRoleCommand(
            17, 42, 43, LeaseManagementPartyRole.CoTenant, new DateOnly(2026, 8, 20),
            false, TenantAccessDisposition.RevokeImmediately, null, null, false,
            true, 51, null, "Change household role", 5,
            SessionId, 12, 3, "ignored-role-delivery"),
        new GrantTenantUserAccessCommand(
            17, 42, 43, "Grant access", "https://rentalcommand.test", 5,
            SessionId, 12, 3, "ignored-grant-delivery"),
        new RevokeTenantUserAccessCommand(
            17, 42, 43, 47, "Revoke access", 5,
            SessionId, 12, 3, "ignored-revoke-delivery"),
    ];
}
