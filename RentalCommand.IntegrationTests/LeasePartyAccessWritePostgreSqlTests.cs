using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class LeasePartyAccessWritePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime InterceptorNow =
        new(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId =
        Guid.Parse("16e9cd8d-4f2b-43fa-9707-64da4ddd85f4");
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public LeasePartyAccessWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(InterceptorNow));
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

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
    public async Task AddEndChangeRole_ReplayExactly_UseInterceptorAuditClock_AndRejectStaleReplay()
    {
        var scenario = await SeedScenarioAsync();
        var add = new AddEffectivePartyCommand(
            1, scenario.RelationshipId, scenario.AddTenantId, LeaseManagementPartyRole.Occupant,
            scenario.BusinessDate, false, "Add occupant", false, null, null,
            scenario.ActorUserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, "party-add-replay");
        var end = new EndEffectivePartyCommand(
            1, scenario.RelationshipId, scenario.EndPartyId, scenario.BusinessDate.AddDays(-1),
            TenantAccessDisposition.RevokeImmediately, null, false, null, null, "End occupant",
            scenario.ActorUserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, "party-end-replay");
        var change = new ChangeEffectivePartyRoleCommand(
            1, scenario.RelationshipId, scenario.ChangePartyId, LeaseManagementPartyRole.CoTenant,
            scenario.BusinessDate, false, TenantAccessDisposition.RevokeImmediately,
            null, null, false, true, scenario.LegalBasisAgreementId, null, "Promote occupant",
            scenario.ActorUserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, "party-change-role-replay");

        var addOutcomes = await ExecuteTwiceAsync(add, "add-replay");
        var endOutcomes = await ExecuteTwiceAsync(end, "end-replay");
        var changeOutcomes = await ExecuteTwiceAsync(change, "change-role-replay");

        foreach (var outcomes in new[] { addOutcomes, endOutcomes, changeOutcomes })
        {
            outcomes.Select(row => row.Disposition).Should().Equal(
                AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed);
            outcomes[1].Value.Should().BeEquivalentTo(outcomes[0].Value);
            outcomes[0].Value.Outcome.Should().Be(LeasePartyMutationOutcome.Applied);
        }

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LeaseManagementParties.CountAsync(row =>
            row.LeaseManagementId == scenario.RelationshipId
            && row.TenantId == scenario.AddTenantId)).Should().Be(1);
        (await _context.Db.LeaseManagementParties.SingleAsync(row =>
            row.Id == scenario.EndPartyId)).EffectiveThrough.Should().Be(scenario.BusinessDate.AddDays(-1));
        (await _context.Db.LeaseManagementParties.CountAsync(row =>
            row.LeaseManagementId == scenario.RelationshipId
            && row.TenantId == scenario.ChangeTenantId
            && row.Role == LeaseManagementPartyRole.CoTenant)).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == "lease-management.party.add"
            || row.CommandType == "lease-management.party.end"
            || row.CommandType == "lease-management.party.change-role")).Should().Be(3);
        (await _context.Db.AtomicAuditLogs
            .Where(row => row.CommandType == "lease-management.party.add"
                || row.CommandType == "lease-management.party.end"
                || row.CommandType == "lease-management.party.change-role")
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync()).Should().OnlyContain(timestamp => timestamp == InterceptorNow);

        var access = await _context.Db.WorkspaceAccessContexts.SingleAsync(row =>
            row.Id == scenario.AccessContextId);
        access.AdvanceRevision(access.AccessRevision);
        access.UpdatedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var staleReplays = new Func<Task>[]
        {
            async () => await ExecuteAsync(add, "add-replay"),
            async () => await ExecuteAsync(end, "end-replay"),
            async () => await ExecuteAsync(change, "change-role-replay"),
        };
        foreach (var staleReplay in staleReplays)
        {
            await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    private async Task<AtomicCommandOutcome<LeasePartyMutationResult>[]> ExecuteTwiceAsync<TCommand>(
        TCommand command, string digest)
        where TCommand : notnull, ILeasePartyAccessCommand =>
        [await ExecuteAsync(command, digest), await ExecuteAsync(command, digest)];

    private async Task<AtomicCommandOutcome<LeasePartyMutationResult>> ExecuteAsync<TCommand>(
        TCommand command, string digest)
        where TCommand : notnull, ILeasePartyAccessCommand
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = LeasePartyAccessWriteSupport.Write(
            command,
            (request, context, token) => ExecuteHandlerAsync(db, request, context, token),
            (request, context, token) => AuthorizeAsync(db, request, context, token));
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(LeasePartyAccessWriteSupport.IdempotencyKey(command, digest), write);
    }

    private static Task<LeasePartyMutationResult> ExecuteHandlerAsync<TCommand>(
        RentalCommandDbContext db, TCommand command, IAtomicCommandContext context, CancellationToken ct)
        where TCommand : notnull, ILeasePartyAccessCommand => command switch
        {
            AddEffectivePartyCommand value => AddEffectivePartyRule.ExecuteAsync(db, value, context, ct),
            EndEffectivePartyCommand value => EndEffectivePartyRule.ExecuteAsync(db, value, context, ct),
            ChangeEffectivePartyRoleCommand value =>
                ChangeEffectivePartyRoleRule.ExecuteAsync(db, value, context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static Task AuthorizeAsync<TCommand>(
        RentalCommandDbContext db, TCommand command, IAtomicCommandContext context, CancellationToken ct)
        where TCommand : notnull, ILeasePartyAccessCommand => command switch
        {
            AddEffectivePartyCommand value => AddEffectivePartyRule.AuthorizeAsync(db, value, context, ct),
            EndEffectivePartyCommand value => EndEffectivePartyRule.AuthorizeAsync(db, value, context, ct),
            ChangeEffectivePartyRoleCommand value =>
                ChangeEffectivePartyRoleRule.AuthorizeAsync(db, value, context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private async Task<Scenario> SeedScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var businessDate = DateOnly.FromDateTime(now);
        var unique = Guid.NewGuid().ToString("N")[..10];
        var actor = new ApplicationUser
        {
            UserName = $"party-writer-{unique}@example.test",
            NormalizedUserName = $"PARTY-WRITER-{unique.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"party-writer-{unique}@example.test",
            NormalizedEmail = $"PARTY-WRITER-{unique.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = "Party Writer", SecurityStamp = unique, ConcurrencyStamp = unique,
            CreatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = 1, Name = "Party executor property", AddressLine1 = "7 Executor Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = unique,
            CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.AddRange(actor, property, unit);
        await _context.Db.SaveChangesAsync();

        var access = new WorkspaceAccessContext
        {
            UserId = actor.Id, PortfolioId = 1, Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = access, PortfolioId = 1, Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(row =>
                row.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), User = actor, ActiveAccessContext = access,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = $"LM-PARTY-{unique}", CreatedAtUtc = now,
            UpdatedAtUtc = now, CreatedByUserId = actor.Id,
        };
        var tenants = Enumerable.Range(1, 4).Select(index => new Tenant
        {
            PortfolioId = 1, FirstName = $"Party{index}", LastName = "Resident",
            Email = $"party-{unique}-{index}@example.test", CreatedAt = now, UpdatedAt = now,
        }).ToArray();
        _context.Db.AddRange(assignment, session, relationship);
        _context.Db.AddRange(tenants);
        await _context.Db.SaveChangesAsync();

        var primary = NewParty(relationship.Id, tenants[0].Id, LeaseManagementPartyRole.PrimaryTenant);
        var endParty = NewParty(relationship.Id, tenants[1].Id, LeaseManagementPartyRole.Occupant);
        var changeParty = NewParty(relationship.Id, tenants[2].Id, LeaseManagementPartyRole.Occupant);
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"party-change-{unique}", RendererKey = "test", RendererVersion = 1,
            SnapshotPayload = "{}", CreatedAtUtc = now, CreatedByUserId = actor.Id,
        };
        _context.Db.AddRange(primary, endParty, changeParty, source);
        await _context.Db.SaveChangesAsync();

        var original = Agreement(relationship.Id, source.Id, 1, LeaseAgreementChangeType.Initial, null);
        _context.Db.Add(original);
        await _context.Db.SaveChangesAsync();
        var legalBasis = Agreement(
            relationship.Id, source.Id, 2, LeaseAgreementChangeType.Restatement, original.Id);
        legalBasis.GoverningFromOn = businessDate;
        legalBasis.Signers.Add(new LeaseAgreementSigner
        {
            PortfolioId = 1, TenantId = changeParty.TenantId,
            LeaseManagementPartyId = changeParty.Id, SignerRole = LeaseLegalSignerRole.CoTenant,
            NameSnapshot = "Party3 Resident", EmailSnapshot = tenants[2].Email!,
            SigningOrder = 1, IsRequired = true,
        });
        _context.Db.Add(legalBasis);
        await _context.Db.SaveChangesAsync();
        var issuedFile = StoredFile("issued");
        var executedFile = StoredFile("executed");
        var issuedArtifact = Artifact(issuedFile, LegalDocumentArtifactKind.IssuedAgreement, 'a');
        var executedArtifact = Artifact(executedFile, LegalDocumentArtifactKind.ExecutedAgreement, 'b');
        _context.Db.AddRange(issuedArtifact, executedArtifact);
        await _context.Db.SaveChangesAsync();
        legalBasis.IssuedArtifactId = issuedArtifact.Id;
        legalBasis.IssuedAtUtc = now;
        legalBasis.ExecutedArtifactId = executedArtifact.Id;
        legalBasis.FullyExecutedAtUtc = now;
        original.SupersededEffectiveOn = businessDate;
        original.SupersededByAgreementId = legalBasis.Id;
        original.SupersessionRecordedAtUtc = now;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        return new Scenario(actor.Id, session.Id, access.Id, access.AccessRevision,
            relationship.Id, tenants[3].Id, endParty.Id, changeParty.Id, changeParty.TenantId,
            legalBasis.Id, businessDate);

        LeaseManagementParty NewParty(
            int relationshipId, int tenantId, LeaseManagementPartyRole role) => new()
        {
            PortfolioId = 1, LeaseManagementId = relationshipId, TenantId = tenantId, Role = role,
            EffectiveFrom = businessDate.AddDays(-30), ChangeReason = "Initial household",
            CreatedAtUtc = now, CreatedByUserId = actor.Id,
        };

        LeaseAgreement Agreement(
            int relationshipId, int sourceId, int version, LeaseAgreementChangeType changeType,
            int? replacesId) => new()
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, LeaseManagementId = relationshipId,
            VersionNumber = version, AgreementNumber = $"AGR-{unique}-{version}",
            ChangeType = changeType, ReplacesAgreementId = replacesId,
            TermType = LeaseAgreementTermType.MonthToMonth,
            TermStartOn = businessDate.AddDays(-30), GoverningFromOn = businessDate.AddDays(-30),
            BaseRentAmount = 1000, RentDueDay = 1, Currency = "USD", TermsSchemaVersion = 1,
            TermsPayload = "{}", DocumentSourceVersionId = sourceId,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = actor.Id,
        };

        StoredFile StoredFile(string kind) => new()
        {
            PortfolioId = 1, FileName = $"{kind}-{unique}.pdf",
            FilePath = $"agreements/{kind}-{unique}.pdf", ContentType = "application/pdf",
            FileSize = 1, EntityType = nameof(LeaseAgreement), EntityId = legalBasis.Id,
            UploadedAt = now,
        };

        LegalDocumentArtifact Artifact(
            StoredFile file, LegalDocumentArtifactKind kind, char hashCharacter) => new()
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, StoredFile = file, ArtifactKind = kind,
            StorageKey = file.FilePath, FileName = file.FileName, ContentType = file.ContentType,
            ByteLength = file.FileSize, ContentSha256 = new string(hashCharacter, 64),
            LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
                ? new string('c', 64)
                : null,
            CreatedAtUtc = now, CreatedByUserId = actor.Id,
        };
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

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:lease-party-access";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        int RelationshipId,
        int AddTenantId,
        int EndPartyId,
        int ChangePartyId,
        int ChangeTenantId,
        int LegalBasisAgreementId,
        DateOnly BusinessDate);
}
