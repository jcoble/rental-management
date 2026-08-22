using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Esign;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

public sealed class NativeEsignInvitationResendAtomicTests : IAsyncLifetime
{
    private const int ActorUserId = 81_500;

    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private Scenario _scenario = default!;
    private readonly AdvisoryLockRecorder _locks = new();

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .AddInterceptors(_locks)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _scenario = await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agreement_resend_replay_stages_exactly_one_new_invitation_without_mutating_packet_history()
    {
        SkipIfNoDocker();
        var actionDigest = new string('a', 64);
        var command = Command(
            NativeEsignInvitationParentKind.LeaseAgreement,
            _scenario.Agreement.ParentId,
            _scenario.Agreement.LegalSignerId,
            actionDigest);
        var identity = new AtomicCommandIdentity(
            "lease-agreement.esign-invitation.resend",
            $"{_scenario.PortfolioId}:{_scenario.LeaseManagementId}:{command.ParentId}:{command.LegalSignerId}:{actionDigest}");

        _locks.Reset();
        var first = await ExecuteAsync(identity, command);
        var replay = await ExecuteAsync(identity, command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        _locks.AggregateIds.Should().Equal(_scenario.LeaseManagementId);
        first.Value.SignatureRequestId.Should().Be(_scenario.Agreement.SignatureRequestId);
        first.Value.SignatureSignerId.Should().Be(_scenario.Agreement.SignatureSignerId);
        first.Value.OutboxIdempotencyKey.Should().Be(
            $"agreement-esign:{_scenario.Agreement.SignatureRequestId}:signer:{_scenario.Agreement.SignatureSignerId}:resend:{actionDigest}");

        await using var db = NewContext();
        var invitations = await db.OutboxMessages.AsNoTracking()
            .Where(message => message.IdempotencyKey == _scenario.Agreement.OriginalOutboxKey
                || message.IdempotencyKey == first.Value.OutboxIdempotencyKey)
            .OrderBy(message => message.Id)
            .ToListAsync();
        invitations.Should().HaveCount(2);
        invitations[1].PortfolioId.Should().Be(_scenario.PortfolioId);
        invitations[1].MessageType.Should().Be("email");
        invitations[1].Payload.Should().Be(invitations[0].Payload);
        invitations[1].AttemptCount.Should().Be(0);
        invitations[1].CreatedAtUtc.Should().Be(invitations[1].NextAttemptAtUtc);
        invitations[1].AcceptedAtUtc.Should().BeNull();
        invitations[1].DeadLetteredAtUtc.Should().BeNull();

        using var payload = JsonDocument.Parse(invitations[1].Payload);
        payload.RootElement.GetProperty("source").GetString()
            .Should().Be(OutboxPayloadSources.LeaseEsignSigningLink);
        payload.RootElement.GetProperty("signatureRequestId").GetInt32()
            .Should().Be(_scenario.Agreement.SignatureRequestId);
        payload.RootElement.GetProperty("leaseManagementId").GetInt32()
            .Should().Be(_scenario.LeaseManagementId);
        payload.RootElement.GetProperty("leaseAgreementId").GetInt32()
            .Should().Be(_scenario.Agreement.ParentId);
        payload.RootElement.GetProperty("to").GetString().Should().Be("agreement@example.test");

        var request = await db.SignatureRequests.AsNoTracking()
            .SingleAsync(item => item.Id == _scenario.Agreement.SignatureRequestId);
        var signer = await db.Set<SignatureSigner>().AsNoTracking()
            .SingleAsync(item => item.Id == _scenario.Agreement.SignatureSignerId);
        request.Status.Should().Be(SignatureRequestStatus.DeliveryFailed);
        request.FailureCode.Should().Be("email-undelivered");
        signer.Status.Should().Be(SignatureSignerStatus.Pending);
        signer.TokenHash.Should().Be(_scenario.Agreement.TokenHash);
        (await db.Set<SignatureAuditEvent>().CountAsync(item =>
            item.SignatureRequestId == request.Id)).Should().Be(_scenario.Agreement.AuditCount);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Addendum_resend_stages_one_new_invitation_from_the_original_frozen_delivery()
    {
        SkipIfNoDocker();
        var actionDigest = new string('b', 64);
        var command = Command(
            NativeEsignInvitationParentKind.LeaseAddendum,
            _scenario.Addendum.ParentId,
            _scenario.Addendum.LegalSignerId,
            actionDigest);
        var identity = new AtomicCommandIdentity(
            "lease-addendum.esign-invitation.resend",
            $"{_scenario.PortfolioId}:{_scenario.LeaseManagementId}:{command.ParentId}:{command.LegalSignerId}:{actionDigest}");

        var outcome = await ExecuteAsync(identity, command);
        var replay = await ExecuteAsync(identity, command with { });

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(outcome.Value);
        outcome.Value.OutboxIdempotencyKey.Should().Be(
            $"addendum-esign:{_scenario.Addendum.SignatureRequestId}:signer:{_scenario.Addendum.SignatureSignerId}:resend:{actionDigest}");
        await using var db = NewContext();
        var original = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.IdempotencyKey == _scenario.Addendum.OriginalOutboxKey);
        var resend = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.IdempotencyKey == outcome.Value.OutboxIdempotencyKey);
        resend.Payload.Should().Be(original.Payload);
        using var payload = JsonDocument.Parse(resend.Payload);
        payload.RootElement.GetProperty("leaseAddendumId").GetInt32()
            .Should().Be(_scenario.Addendum.ParentId);
        payload.RootElement.GetProperty("to").GetString().Should().Be("addendum@example.test");
        (await db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == outcome.Value.OutboxIdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task LegacyAgreementResendReceipt_ReplaysStoredSharedResendShapeThroughRequestExecutor()
    {
        SkipIfNoDocker();
        var command = Command(
            NativeEsignInvitationParentKind.LeaseAgreement,
            _scenario.Agreement.ParentId,
            _scenario.Agreement.LegalSignerId,
            new string('c', 64));
        const string key = "legacy-agreement-resend";
        var stored = new ResendNativeEsignInvitationResult(
            _scenario.Agreement.SignatureRequestId,
            _scenario.Agreement.SignatureSignerId,
            "stored-legacy-resend-key");
        await using var descriptorDb = NewContext();
        var write = NativeEsignWriteSupport.Write<ResendNativeEsignInvitationCommand,
            ResendNativeEsignInvitationResult>(descriptorDb, command);
        var codec = new AtomicJsonResultCodec<ResendNativeEsignInvitationResult>(write.ResultContract);
        await using (var db = NewContext())
        {
            db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = write.OperationName,
                IdempotencyKey = key, RequestFingerprint = AtomicCommandFingerprint.Create(command),
                Status = AtomicCommandReceiptStatus.Completed, ResultContract = write.ResultContract,
                ResultJson = codec.Serialize(stored), StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var scope = _services!.CreateAsyncScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var replay = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, NativeEsignWriteSupport.Write<ResendNativeEsignInvitationCommand,
                ResendNativeEsignInvitationResult>(scopedDb, command));

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    [SkippableFact]
    public async Task AddendumIssue_ExecutesThroughRequestExecutor()
    {
        SkipIfNoDocker();
        var now = DateTime.UtcNow;
        await using var arrange = NewContext();
        var baseAgreement = await arrange.LeaseAgreements.SingleAsync(row =>
            row.Id == _scenario.Agreement.ParentId);
        baseAgreement.ExecutedArtifactId = baseAgreement.IssuedArtifactId;
        baseAgreement.FullyExecutedAtUtc = now;
        var addendum = new LeaseAddendum
        {
            PublicId = Guid.NewGuid(), SeriesPublicId = Guid.NewGuid(),
            PortfolioId = _scenario.PortfolioId, LeaseManagementId = _scenario.LeaseManagementId,
            BaseAgreementId = baseAgreement.Id, VersionNumber = 1,
            AddendumNumber = "ADD-EXECUTOR-ISSUE", Purpose = LeaseAddendumPurpose.Other,
            EffectiveFromOn = DateOnly.FromDateTime(now), TermsSchemaVersion = 1,
            TermsPayload = "{}", DocumentSourceVersionId = baseAgreement.DocumentSourceVersionId,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = ActorUserId,
        };
        var signer = new LeaseAddendumSigner
        {
            PortfolioId = _scenario.PortfolioId, LeaseAddendum = addendum,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant, NameSnapshot = "Issue Signer",
            EmailSnapshot = "issue-addendum@example.test", SigningOrder = 1, IsRequired = true,
        };
        arrange.AddRange(addendum, signer);
        await arrange.SaveChangesAsync();

        const string fileName = "issued-addendum.pdf";
        const string storageKey = "legal/addenda/issued-addendum.pdf";
        var contentSha = new string('d', 64);
        var fingerprint = LegalDocumentIssuanceBinding.CreateAddendum(
            _scenario.PortfolioId, _scenario.LeaseManagementId, addendum.Id,
            addendum.DraftRevision, addendum.DocumentSourceVersionId, addendum.TermsSchemaVersion,
            addendum.TermsPayload, [], contentSha, 8, fileName);
        var pendingId = Guid.NewGuid();
        arrange.PendingFileUploads.Add(new PendingFileUpload
        {
            Id = pendingId, PortfolioId = _scenario.PortfolioId, ActorScopeId = ActorUserId,
            Purpose = LegalDocumentIssuanceBinding.AddendumUploadPurpose,
            OperationKeyHash = new string('e', 64), RequestFingerprint = fingerprint,
            StoragePath = storageKey, FileName = fileName, ContentType = "application/pdf",
            SizeBytes = 8, State = PendingFileUploadState.Prepared,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        });
        await arrange.SaveChangesAsync();
        var command = new IssueLeaseAddendumCommand(
            pendingId, addendum.DocumentSourceVersionId, fingerprint, _scenario.PortfolioId,
            _scenario.LeaseManagementId, addendum.Id, addendum.DraftRevision, "issue-delivery",
            "Executor addendum", storageKey, fileName, 8, contentSha,
            "https://example.test", [new NativeEsignAddendumSignerCommand(signer.Id)],
            ActorUserId, _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision);

        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync("issue-addendum-executor",
                NativeEsignWriteSupport.Write<IssueLeaseAddendumCommand,
                    IssueLeaseAddendumResult>(db, command));

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        const string legacyKey = "legacy-addendum-issue";
        var issueCodec = new AtomicJsonResultCodec<IssueLeaseAddendumResult>("lease-addendum.issue.v1");
        await using (var receiptDb = NewContext())
        {
            receiptDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = "lease-addendum.issue",
                IdempotencyKey = legacyKey, RequestFingerprint = AtomicCommandFingerprint.Create(command),
                Status = AtomicCommandReceiptStatus.Completed, ResultContract = issueCodec.ContractName,
                ResultJson = issueCodec.Serialize(outcome.Value), StartedAt = now, CompletedAt = now,
            });
            await receiptDb.SaveChangesAsync();
        }
        var legacyReplay = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(legacyKey, NativeEsignWriteSupport.Write<IssueLeaseAddendumCommand,
                IssueLeaseAddendumResult>(db, command));
        legacyReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        legacyReplay.Value.Should().BeEquivalentTo(outcome.Value);

        await using var verify = NewContext();
        (await verify.SignatureRequests.CountAsync(row =>
            row.LeaseAddendumId == addendum.Id)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Resend_rejects_fully_signed_declined_voided_and_already_signed_targets()
    {
        SkipIfNoDocker();
        var cases = new[]
        {
            (_scenario.FullySigned, "The signing packet is fully signed and cannot be resent."),
            (_scenario.Declined, "The signing packet was declined and cannot be resent."),
            (_scenario.Voided, "The signing packet was voided or canceled and cannot be resent."),
            (_scenario.AlreadySigned, "This signer has already signed and cannot receive another invitation."),
        };

        int outboxCount;
        await using (var before = NewContext())
        {
            outboxCount = await before.OutboxMessages.CountAsync();
        }

        for (var index = 0; index < cases.Length; index++)
        {
            var (target, expectedMessage) = cases[index];
            var digest = index.ToString("x64");
            var command = Command(
                NativeEsignInvitationParentKind.LeaseAgreement,
                target.ParentId,
                target.LegalSignerId,
                digest);
            var identity = new AtomicCommandIdentity(
                "lease-agreement.esign-invitation.resend",
                $"reject:{target.ParentId}:{target.LegalSignerId}:{digest}");

            var action = () => ExecuteAsync(identity, command);

            await action.Should().ThrowAsync<DomainValidationException>()
                .WithMessage(expectedMessage);
        }

        await using var verify = NewContext();
        (await verify.OutboxMessages.CountAsync()).Should().Be(outboxCount);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "lease-agreement.esign-invitation.resend"
            && receipt.IdempotencyKey.StartsWith("reject:"))).Should().Be(0);
    }

    private async Task<AtomicCommandOutcome<ResendNativeEsignInvitationResult>> ExecuteAsync(
        AtomicCommandIdentity identity,
        ResendNativeEsignInvitationCommand command)
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey,
                NativeEsignWriteSupport.Write<ResendNativeEsignInvitationCommand,
                    ResendNativeEsignInvitationResult>(db, command));
    }

    private ResendNativeEsignInvitationCommand Command(
        NativeEsignInvitationParentKind parentKind,
        int parentId,
        int legalSignerId,
        string deliveryIdempotencyKey) => new(
        _scenario.PortfolioId,
        _scenario.LeaseManagementId,
        parentKind,
        parentId,
        legalSignerId,
        ActorUserId,
        _scenario.SessionId,
        _scenario.AccessContextId,
        _scenario.AccessRevision,
        deliveryIdempotencyKey);

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private static async Task<Scenario> SeedAsync(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "esign-resend@example.test",
            NormalizedUserName = "ESIGN-RESEND@EXAMPLE.TEST",
            Email = "esign-resend@example.test",
            NormalizedEmail = "ESIGN-RESEND@EXAMPLE.TEST",
            DisplayName = "E-sign Resend Operator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = "E-sign Resend Portfolio",
            ManagementCompanyName = "E-sign Resend Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = "Invitation House",
            AddressLine1 = "1 Delivery Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            RelationshipNumber = "REL-ESIGN-RESEND",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            RowVersion = Guid.NewGuid(),
        };
        var sourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
            portfolio.Id,
            ActorUserId,
            now,
            "esign-resend-test-renderer");
        sourceVersion.Portfolio = portfolio;
        sourceVersion.CreatedByUserId = ActorUserId;
        var agreementFile = StoredFile(portfolio, "agreement-issued.pdf", now);
        var agreementArtifact = Artifact(
            portfolio,
            agreementFile,
            LegalDocumentArtifactKind.IssuedAgreement,
            ActorUserId,
            now);
        var addendumFile = StoredFile(portfolio, "addendum-issued.pdf", now);
        var addendumArtifact = Artifact(
            portfolio,
            addendumFile,
            LegalDocumentArtifactKind.IssuedAddendum,
            ActorUserId,
            now);

        db.AddRange(user, portfolio, property, unit, relationship, sourceVersion,
            agreementArtifact, addendumArtifact);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);

        var agreements = Enumerable.Range(1, 5)
            .Select(version => Agreement(
                portfolio.Id,
                relationship.Id,
                sourceVersion.Id,
                version,
                now))
            .ToArray();
        for (var index = 1; index < agreements.Length; index++)
        {
            agreements[index].ChangeType = LeaseAgreementChangeType.Correction;
            agreements[index].ReplacesAgreement = agreements[index - 1];
            agreements[index].CorrectionReason = $"Test resend eligibility state {index + 1}.";
        }
        db.AddRange(agreements);
        await db.SaveChangesAsync();

        var agreement = await AddAgreementPacketAsync(
            db, agreements[0], SignatureRequestStatus.DeliveryFailed,
            [(SignatureSignerStatus.Pending, "agreement@example.test")],
            failureCode: "email-undelivered", agreementArtifact.Id, now);
        var fullySigned = await AddAgreementPacketAsync(
            db, agreements[1], SignatureRequestStatus.ExecutionPending,
            [(SignatureSignerStatus.Signed, "fully-signed@example.test")], null, agreementArtifact.Id, now);
        var voided = await AddAgreementPacketAsync(
            db, agreements[2], SignatureRequestStatus.Voided,
            [(SignatureSignerStatus.Pending, "voided@example.test")], null, agreementArtifact.Id, now,
            voidParent: true);
        var declined = await AddAgreementPacketAsync(
            db, agreements[3], SignatureRequestStatus.Declined,
            [(SignatureSignerStatus.Declined, "declined@example.test")], null, agreementArtifact.Id, now);
        var alreadySigned = await AddAgreementPacketAsync(
            db, agreements[4], SignatureRequestStatus.PartiallySigned,
            [
                (SignatureSignerStatus.Signed, "already-signed@example.test"),
                (SignatureSignerStatus.Pending, "still-pending@example.test"),
            ], null, agreementArtifact.Id, now);

        var addendum = new LeaseAddendum
        {
            PublicId = Guid.NewGuid(),
            SeriesPublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id,
            BaseAgreementId = agreements[0].Id,
            VersionNumber = 1,
            AddendumNumber = "ADD-ESIGN-1",
            Purpose = LeaseAddendumPurpose.Other,
            EffectiveFromOn = DateOnly.FromDateTime(now),
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = sourceVersion.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        db.Add(addendum);
        await db.SaveChangesAsync();
        var addendumTarget = await AddAddendumPacketAsync(db, addendum, addendumArtifact.Id, now);

        return new Scenario(
            portfolio.Id,
            relationship.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            agreement,
            addendumTarget,
            fullySigned,
            declined,
            voided,
            alreadySigned);
    }

    private static async Task<InvitationTarget> AddAgreementPacketAsync(
        RentalCommandDbContext db,
        LeaseAgreement agreement,
        SignatureRequestStatus requestStatus,
        IReadOnlyList<(SignatureSignerStatus Status, string Email)> signerStates,
        string? failureCode,
        int issuedArtifactId,
        DateTime now,
        bool voidParent = false)
    {
        var legalSigners = signerStates.Select((state, index) => new LeaseAgreementSigner
        {
            PortfolioId = agreement.PortfolioId,
            LeaseAgreementId = agreement.Id,
            SignerRole = index == 0 ? LeaseLegalSignerRole.PrimaryTenant : LeaseLegalSignerRole.CoTenant,
            NameSnapshot = $"Agreement Signer {index + 1}",
            EmailSnapshot = state.Email,
            SigningOrder = checked((short)(index + 1)),
            IsRequired = true,
        }).ToArray();
        db.AddRange(legalSigners);
        agreement.IssuedArtifactId = issuedArtifactId;
        agreement.IssuedAtUtc = now;
        if (voidParent)
        {
            agreement.VoidedAtUtc = now;
            agreement.VoidReasonCode = "operator-void";
        }
        await db.SaveChangesAsync();

        var request = new SignatureRequest
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = agreement.PortfolioId,
            LeaseAgreementId = agreement.Id,
            Provider = "native",
            IdempotencyKey = $"agreement-packet:{agreement.Id}",
            Status = requestStatus,
            Subject = $"Agreement {agreement.AgreementNumber}",
            IssuedArtifactId = agreement.IssuedArtifactId!.Value,
            PreparedAtUtc = now,
            ProviderAcceptedAtUtc = now,
            DeclinedAtUtc = requestStatus == SignatureRequestStatus.Declined ? now : null,
            VoidedAtUtc = requestStatus == SignatureRequestStatus.Voided ? now : null,
            FailureCode = failureCode,
            CreatedByUserId = ActorUserId,
        };
        var signatureSigners = legalSigners.Select((legalSigner, index) => new SignatureSigner
        {
            PortfolioId = agreement.PortfolioId,
            AgreementSignerId = legalSigner.Id,
            NameSnapshot = legalSigner.NameSnapshot,
            EmailSnapshot = legalSigner.EmailSnapshot,
            SigningOrder = legalSigner.SigningOrder,
            IsRequired = true,
            TokenHash = (agreement.Id * 10 + index + 1).ToString("x64"),
            TokenExpiresAtUtc = now.AddDays(14),
            Status = signerStates[index].Status,
            ConsentGivenAtUtc = signerStates[index].Status == SignatureSignerStatus.Signed ? now : null,
            SignedAtUtc = signerStates[index].Status == SignatureSignerStatus.Signed ? now : null,
            DeclinedAtUtc = signerStates[index].Status == SignatureSignerStatus.Declined ? now : null,
            SignatureType = signerStates[index].Status == SignatureSignerStatus.Signed
                ? SignatureSignatureType.Typed
                : SignatureSignatureType.None,
            TypedName = signerStates[index].Status == SignatureSignerStatus.Signed
                ? legalSigner.NameSnapshot
                : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }).ToArray();
        request.Signers.AddRange(signatureSigners);
        request.AuditEvents.Add(new SignatureAuditEvent
        {
            PortfolioId = agreement.PortfolioId,
            Type = SignatureAuditEventType.Sent,
            OccurredAtUtc = now,
            Detail = "Original invitation staged.",
        });
        db.Add(request);
        await db.SaveChangesAsync();

        foreach (var signer in signatureSigners)
        {
            db.OutboxMessages.Add(OriginalAgreementInvitation(
                agreement.LeaseManagementId, agreement.Id, request, signer, now));
        }
        await db.SaveChangesAsync();

        var selected = signatureSigners[0];
        return new InvitationTarget(
            agreement.Id,
            legalSigners[0].Id,
            request.Id,
            selected.Id,
            selected.TokenHash,
            $"agreement-esign:{request.Id}:signer:{selected.Id}:invite",
            request.AuditEvents.Count);
    }

    private static async Task<InvitationTarget> AddAddendumPacketAsync(
        RentalCommandDbContext db,
        LeaseAddendum addendum,
        int issuedArtifactId,
        DateTime now)
    {
        var legalSigner = new LeaseAddendumSigner
        {
            PortfolioId = addendum.PortfolioId,
            LeaseAddendumId = addendum.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Addendum Signer",
            EmailSnapshot = "addendum@example.test",
            SigningOrder = 1,
            IsRequired = true,
        };
        db.Add(legalSigner);
        await db.SaveChangesAsync();
        addendum.IssuedArtifactId = issuedArtifactId;
        addendum.IssuedAtUtc = now;
        await db.SaveChangesAsync();
        var request = new SignatureRequest
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = addendum.PortfolioId,
            LeaseAddendumId = addendum.Id,
            Provider = "native",
            IdempotencyKey = $"addendum-packet:{addendum.Id}",
            Status = SignatureRequestStatus.AwaitingSignatures,
            Subject = $"Addendum {addendum.AddendumNumber}",
            IssuedArtifactId = addendum.IssuedArtifactId!.Value,
            PreparedAtUtc = now,
            ProviderAcceptedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var signer = new SignatureSigner
        {
            PortfolioId = addendum.PortfolioId,
            AddendumSignerId = legalSigner.Id,
            NameSnapshot = legalSigner.NameSnapshot,
            EmailSnapshot = legalSigner.EmailSnapshot,
            SigningOrder = 1,
            IsRequired = true,
            TokenHash = (addendum.Id * 100).ToString("x64"),
            TokenExpiresAtUtc = now.AddDays(14),
            Status = SignatureSignerStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        request.Signers.Add(signer);
        request.AuditEvents.Add(new SignatureAuditEvent
        {
            PortfolioId = addendum.PortfolioId,
            Type = SignatureAuditEventType.Sent,
            OccurredAtUtc = now,
            Detail = "Original addendum invitation staged.",
        });
        db.Add(request);
        await db.SaveChangesAsync();
        var original = new OutboxMessage
        {
            PortfolioId = addendum.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new
            {
                source = OutboxPayloadSources.LeaseEsignSigningLink,
                signatureRequestId = request.Id,
                leaseManagementId = addendum.LeaseManagementId,
                leaseAddendumId = addendum.Id,
                to = signer.EmailSnapshot,
                subject = $"Please sign: {request.Subject}",
                body = $"Review and sign: https://example.test/sign/addendum-token-{signer.Id}",
            }),
            IdempotencyKey = $"addendum-esign:{request.Id}:signer:{signer.Id}:invite",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
        db.Add(original);
        await db.SaveChangesAsync();
        return new InvitationTarget(
            addendum.Id,
            legalSigner.Id,
            request.Id,
            signer.Id,
            signer.TokenHash,
            original.IdempotencyKey,
            request.AuditEvents.Count);
    }

    private static OutboxMessage OriginalAgreementInvitation(
        int leaseManagementId,
        int leaseAgreementId,
        SignatureRequest request,
        SignatureSigner signer,
        DateTime now) => new()
        {
            PortfolioId = request.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new
            {
                source = OutboxPayloadSources.LeaseEsignSigningLink,
                signatureRequestId = request.Id,
                leaseManagementId,
                leaseAgreementId,
                to = signer.EmailSnapshot,
                subject = $"Please sign: {request.Subject}",
                body = $"Review and sign: https://example.test/sign/agreement-token-{signer.Id}",
            }),
            IdempotencyKey = $"agreement-esign:{request.Id}:signer:{signer.Id}:invite",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };

    private static LeaseAgreement Agreement(
        int portfolioId,
        int leaseManagementId,
        int sourceVersionId,
        int version,
        DateTime now) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = leaseManagementId,
            VersionNumber = version,
            AgreementNumber = $"AGR-RESEND-{version}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2026, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1).AddDays(version - 1),
            BaseRentAmount = 1_000,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = sourceVersionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };

    private static StoredFile StoredFile(Portfolio portfolio, string fileName, DateTime now) => new()
    {
        Portfolio = portfolio,
        FileName = fileName,
        FilePath = $"test/{Guid.NewGuid():N}/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        Portfolio portfolio,
        StoredFile file,
        LegalDocumentArtifactKind kind,
        int actorUserId,
        DateTime now) => new()
        {
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            StoredFile = file,
            ArtifactKind = kind,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = file.FileSize,
            ContentSha256 = new string('c', 64),
            LegalIssuanceFingerprint = new string('d', 64),
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; invitation resend proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record InvitationTarget(
        int ParentId,
        int LegalSignerId,
        int SignatureRequestId,
        int SignatureSignerId,
        string TokenHash,
        string OriginalOutboxKey,
        int AuditCount);

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        InvitationTarget Agreement,
        InvitationTarget Addendum,
        InvitationTarget FullySigned,
        InvitationTarget Declined,
        InvitationTarget Voided,
        InvitationTarget AlreadySigned);

    private sealed class AdvisoryLockRecorder : DbCommandInterceptor
    {
        public List<int> AggregateIds { get; } = [];

        public void Reset() => AggregateIds.Clear();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                || command.Parameters.Count < 2)
                return;
            if (command.Parameters[command.Parameters.Count - 1].Value is int aggregateId)
                AggregateIds.Add(aggregateId);
        }
    }
}
