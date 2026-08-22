using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Esign;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for abandoning an unissued canonical Agreement successor draft.</summary>
public sealed class LeaseAgreementSuccessorDraftCancellationAtomicTests : IAsyncLifetime
{
    private const int ActorUserId = 790;
    private const string RecoveryCommandType = "lease-agreement.issued-replacement-draft.create";
    private static readonly DateTime FrozenNow = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FrozenSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string FrozenKeyDigest =
        "8d0c5f62e9b92e1578c1611afce9191d7b04aad1bb2957f06937dfd0e950c447";
    // Frozen fingerprints computed once from the command DTO shapes at base 060f7509.
    // Never regenerate these values from the current command model or serialization helpers.
    private const string PrepareFingerprint = "9d5af19a5108d56c127d157ab6544234c76b6addd82956a779499b23c7e7814c";
    private const string EndingFingerprint = "0ebd34bd8f83296a5dcb073daac54046f1b03b53f1ad2738a97d00ee8f71f37a";
    private const string CancelFingerprint = "fa253e5a70bbe4d57d099bed1c1e28474616b830fdb6954aae58bcf0a57c6d3e";
    private const string TransferFingerprint = "5cd8ea8da7a734d1d815d119e98766ad02ad31573d05dc3f5ccb48c40904a377";
    private const string SuccessorCancelFingerprint = "d45adb6abb3d10a5fd9e7bd8220fcb6b374d347b5e66dd3fba69836fe41706e0";
    private const string AddendumCreateFingerprint = "a7612672b19dc0373df585258223e57a6d3fd8a76d0d2d438efcb1366f478a9f";
    private const string AddendumEditFingerprint = "357bd4c9f1ef4f6ba24a17f13cbde524bd352609ea413d172fc269121b295364";
    private const string AddendumCorrectFingerprint = "41ca24d8e608bcc851c988f923e9e45029f663d96cb7c89abce9d03915e62f90";
    private const string AgreementEditFingerprint = "d433c7f3bc6ed2c2aa6e4f07d0a9d27c3554aa1ea7267939a9c39074f5dd386a";
    private const string SuccessorCreateFingerprint = "ff9a143ce4614df69d3840c638397a4a408639acff80648c1b0758d74b6e5933";
    private const string IssuedReplacementFingerprint = "afa9ea1de82c6016b1253292e244212ad826fd4bc7bd70ac96e5bd8dd58c5406";
    private const string DispositionFingerprint = "9ef6c74aebe2c042ecf1164e25af49f59a0b47cd1ee36fb55acf3fdf6dcd599d";
    // Frozen fingerprints computed once from the family (c) command DTO shapes at base f5593364.
    private const string GivePossessionFingerprint = "6c5a3dbd60fb3229f91b767ecbcf0ea99954fdbaa2a7b16dec4385ea9e860cf7";
    private const string ReconcilePossessionFingerprint = "1b710e57b3df467b2329ae1b0d604a91aabfed8dbfca4811a4733afc5eafe22e";
    private const string ConfirmMoveInFingerprint = "d611f7dff89a80f92235be9f02907236c95ae4d39783e690511bc503087a81c6";
    private const string ReturnPossessionFingerprint = "ab2800045a5d483d5ab094a0e4a3e231c79ec054dbef9037fb707788ee10d229";
    private const string CompleteTurnoverFingerprint = "f58e67d7892ec1d10f3987605c330d7a4b2df47c98b16837aa889f25106243f9";
    private const string VoidAgreementFingerprint = "a0d6b224a5568814403599775e682e882477e96b61901669d9aea6307cd0c98c";
    private const string VoidAddendumFingerprint = "8e7bbba69864244b5dfad7c9431c8f7da2b5e967f7ea2b07bfda2a6efb3cdf01";
    private const string CloseAccountFingerprint = "5f286fb2dd323bf80323f302dc09e588d9e5088898ebdca5a9fe9ed8736f31d4";
    private static readonly AtomicJsonResultCodec<CancelLeaseAgreementSuccessorDraftResult> Codec =
        new("lease-agreement.successor-draft.cancel.v1");
    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private IServiceScope? _scope;
    private bool _dockerAvailable;
    private Scenario _scenario = default!;

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
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _scope = _services.CreateScope();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _scenario = await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        _scope?.Dispose();
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Cancel_commits_canonical_facts_releases_successor_slot_and_replays_once()
    {
        SkipIfNoDocker();
        var command = Command("abandon-correction", "The correction is no longer needed.");
        var identity = new AtomicCommandIdentity(
            "lease-agreement.successor-draft.cancel", command.DeliveryIdempotencyKey);

        var first = await ExecuteCancelAsync(identity.IdempotencyKey, command);
        var replay = await ExecuteCancelAsync(identity.IdempotencyKey, command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.Canceled);

        await using var db = NewContext();
        var canceled = await db.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.SuccessorAgreementId);
        canceled.DraftCanceledAtUtc.Should().NotBeNull();
        canceled.DraftCanceledByUserId.Should().Be(ActorUserId);
        canceled.DraftCancellationReason.Should().Be("The correction is no longer needed.");
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);

        db.LeaseAgreements.Add(NewSuccessor(
            _scenario.ReplacementAgreementId,
            3,
            LeaseAgreementChangeType.Restatement,
            correctionReason: null));
        await db.SaveChangesAsync();
        (await db.LeaseAgreements.CountAsync(agreement =>
            agreement.ReplacesAgreementId == _scenario.SourceAgreementId
            && agreement.DraftCanceledAtUtc == null)).Should().Be(1);
    }

    [SkippableFact]
    public async Task EndingDisposition_ExactRetry_DoesNotRepeatTransitionAuditOrOutbox()
    {
        SkipIfNoDocker();
        int unitId;
        await using (var arrange = NewContext())
        {
            var relationship = await arrange.LeaseManagements.SingleAsync(row =>
                row.Id == _scenario.LeaseManagementId);
            relationship.PossessionGivenAtUtc = DateTime.UtcNow.AddMonths(-2);
            unitId = relationship.UnitId;
            await arrange.SaveChangesAsync();
        }

        var noticeAt = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-1), DateTimeKind.Utc);
        var plannedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(30), DateTimeKind.Utc);
        var command = new RecordLeaseEndingDispositionCommand(
            _scenario.PortfolioId,
            _scenario.LeaseManagementId,
            unitId,
            LeaseManagementEndingDisposition.NonRenewalMoveOut,
            noticeAt,
            plannedAt,
            "Resident provided notice.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            $"ending-disposition:retry:{Guid.NewGuid():N}");
        var key = $"{_scenario.PortfolioId}:{_scenario.LeaseManagementId}:ending-retry";
        var before = await EndingCountsAsync(key, command.DeliveryIdempotencyKey);

        var first = await ExecuteEndingAsync(key, command);
        var afterFirst = await EndingCountsAsync(key, command.DeliveryIdempotencyKey);
        var replay = await ExecuteEndingAsync(key, command);
        var afterReplay = await EndingCountsAsync(key, command.DeliveryIdempotencyKey);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(RecordLeaseEndingDispositionOutcome.Recorded);
        afterFirst.Receipts.Should().Be(before.Receipts + 1);
        afterFirst.Audits.Should().Be(before.Audits + 1);
        afterFirst.OutboxMessages.Should().Be(before.OutboxMessages + 1);
        afterReplay.Should().Be(afterFirst);
    }

    [SkippableFact]
    public async Task PropertyDisposition_ExactRetry_DoesNotRepeatTransitionAuditOrOutbox()
    {
        SkipIfNoDocker();
        int propertyId;
        await using (var arrange = NewContext())
        {
            propertyId = await arrange.LeaseManagements
                .Where(row => row.Id == _scenario.LeaseManagementId)
                .Select(row => row.PropertyId)
                .SingleAsync();
        }

        var deliveryKey = $"property-disposition:{_scenario.PortfolioId}:{propertyId}:{Guid.NewGuid():N}";
        var command = new CreatePropertyDispositionCommand(
            _scenario.PortfolioId,
            propertyId,
            DateTime.UtcNow.Date,
            250_000m,
            12_500m,
            "Replay Buyer",
            "Exact retry proof",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            deliveryKey);
        var before = await PropertyDispositionCountsAsync(propertyId, deliveryKey);

        var first = await ExecutePropertyDispositionAsync(deliveryKey, command);
        var afterFirst = await PropertyDispositionCountsAsync(propertyId, deliveryKey);
        var replay = await ExecutePropertyDispositionAsync(deliveryKey, command);
        var afterReplay = await PropertyDispositionCountsAsync(propertyId, deliveryKey);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(CreatePropertyDispositionOutcome.Created);
        afterFirst.Dispositions.Should().Be(before.Dispositions + 1);
        afterFirst.Receipts.Should().Be(before.Receipts + 1);
        afterFirst.Audits.Should().BeGreaterThan(before.Audits);
        afterFirst.OutboxMessages.Should().BeGreaterThan(before.OutboxMessages);
        afterReplay.Should().Be(afterFirst);

        await using var assert = NewContext();
        (await assert.Properties.AsNoTracking()
            .Where(row => row.Id == propertyId)
            .Select(row => row.Status)
            .SingleAsync()).Should().Be(PropertyStatus.Inactive);
        (await assert.UnitOperationalPeriods.AsNoTracking().CountAsync(row =>
            row.PropertyId == propertyId
            && row.Type == UnitOperationalPeriodType.ManagementHold
            && row.EndedAtUtc == null)).Should().Be(1);

        var closedManagement = await assert.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == _scenario.LeaseManagementId);
        closedManagement.CanceledAtUtc.Should().NotBeNull();
        closedManagement.CancellationReasonCode.Should().Be("PropertyDisposed");

        var openAccountManagement = await assert.LeaseManagements.AsNoTracking()
            .SingleAsync(row => row.Id == _scenario.LeaseManagementId + 1);
        openAccountManagement.PossessionReturnedAtUtc.Should().BeNull();
        openAccountManagement.CanceledAtUtc.Should().BeNull();
        openAccountManagement.AccountClosedAtUtc.Should().BeNull();
        (await assert.TenantAccounts.AsNoTracking()
            .Where(row => row.LeaseManagementId == openAccountManagement.Id)
            .Select(row => row.ClosedAtUtc)
            .SingleAsync()).Should().BeNull();
    }

    [SkippableFact]
    public async Task MigratedExecutor_ReplaysFrozenLegacyReceiptsIncludingPossessionFamily()
    {
        SkipIfNoDocker();
        _scenario.PortfolioId.Should().Be(70_001);
        _scenario.LeaseManagementId.Should().Be(10_000);
        _scenario.SourceAgreementId.Should().Be(20_000);
        _scenario.SuccessorAgreementId.Should().Be(20_001);
        _scenario.IssuedAgreementId.Should().Be(20_011);
        int propertyId;
        int sourceUnitId;
        int destinationUnitId;
        await using (var arrange = NewContext())
        {
            var relationship = await arrange.LeaseManagements.SingleAsync(row =>
                row.Id == _scenario.LeaseManagementId);
            propertyId = relationship.PropertyId;
            sourceUnitId = relationship.UnitId;
            var destination = new Unit
            {
                Id = 70_007,
                PortfolioId = _scenario.PortfolioId,
                PropertyId = propertyId,
                UnitNumber = $"legacy-replay-{Guid.NewGuid():N}"[..20],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            arrange.Units.Add(destination);
            await arrange.SaveChangesAsync();
            destinationUnitId = destination.Id;
        }

        var businessNow = FrozenNow;
        var businessDate = DateOnly.FromDateTime(businessNow);
        var prepare = new PrepareMoveInCommand(
            _scenario.PortfolioId, null, sourceUnitId, ActorUserId, _scenario.SessionId,
            _scenario.AccessContextId, _scenario.AccessRevision, null, businessDate, [], null,
            LeaseAgreementTermType.FixedTerm, businessDate, businessDate.AddYears(1), 1000m, 1,
            500m, 25m, 5, 1, "{}", false, null, null, null,
            $"prepare-move-in:70001:unit:70003:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.prepare-move-in",
            $"70001:unit:70003:{FrozenKeyDigest}", prepare, PrepareFingerprint,
            """{"Outcome":0,"ApplicationId":null,"LeaseManagementId":11,"TenantAccountId":12,"LeaseAgreementId":13,"OpeningBalanceLedgerEntryId":null,"SecurityDepositAccountId":null,"TenantIds":[14],"LeaseManagementPartyIds":[15],"LeaseAgreementSignerIds":[16],"Error":null}""",
            new PrepareMoveInResult(PrepareMoveInOutcome.Prepared, null, 11, 12, 13, null, null,
                [14], [15], [16], null), "lease-management.prepare-move-in.v2");

        var ending = new RecordLeaseEndingDispositionCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId,
            LeaseManagementEndingDisposition.NonRenewalMoveOut, businessNow, businessNow.AddDays(30),
            "Frozen replay", ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
            _scenario.AccessRevision, $"ending-disposition:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.ending-disposition",
            $"70001:10000:{FrozenKeyDigest}", ending, EndingFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"EndingDisposition":3,"EndingDispositionDecidedAtUtc":"2026-08-22T12:00:00Z","EndingDispositionDecidedByUserId":790,"NoticeGivenAtUtc":"2026-08-22T12:00:00Z","PlannedMoveOutAtUtc":"2026-09-21T12:00:00Z","Error":null}""",
            new RecordLeaseEndingDispositionResult(RecordLeaseEndingDispositionOutcome.Recorded,
                _scenario.LeaseManagementId, LeaseManagementEndingDisposition.NonRenewalMoveOut,
                businessNow, ActorUserId, businessNow, businessNow.AddDays(30), null),
            "lease-management.ending-disposition.v1");

        var cancel = new CancelPlannedRelationshipCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId, ActorUserId,
            _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision, businessNow,
            "USER_REQUEST", "Frozen replay", "Frozen replay", [],
            $"cancel-planned:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.cancel-planned",
            $"70001:10000:{FrozenKeyDigest}", cancel, CancelFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"CanceledAtUtc":"2026-08-22T12:00:00Z","AccountClosedAtUtc":"2026-08-22T12:00:00Z","CanceledAgreementDraftIds":[],"CanceledAddendumDraftIds":[],"RevokedAccessIds":[],"RetainedAccessIds":[],"Error":null}""",
            new CancelPlannedRelationshipResult(CancelPlannedRelationshipOutcome.Canceled,
                _scenario.LeaseManagementId, sourceUnitId, businessNow, businessNow,
                [], [], [], [], null), "lease-management.cancel-planned.v1");

        var transfer = new TransferLeaseManagementCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId, destinationUnitId,
            ActorUserId, _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
            businessNow, Guid.Parse("625f0c8d-b9e9-152e-78c1-611afce9191d"), businessDate,
            null, false, null, 1, true, true,
            "Frozen replay", $"unit-transfer:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.transfer-unit",
            $"70001:10000:{FrozenKeyDigest}", transfer, TransferFingerprint,
            """{"Outcome":0,"TransferPublicId":"625f0c8d-b9e9-152e-78c1-611afce9191d","SourceLeaseManagementId":10000,"SourceUnitId":70003,"DestinationLeaseManagementId":21,"DestinationUnitId":70007,"DestinationTenantAccountId":22,"DestinationAgreementId":23,"DestinationSecurityDepositAccountId":null,"TurnoverPeriodId":24,"SourcePossessionReturnedAtUtc":"2026-08-22T12:00:00Z","DestinationPossessionGivenAtUtc":null,"CarriedTenantBalance":0,"CarriedSecurityDeposit":0,"EndedSourcePartyIds":[],"DestinationPartyIds":[],"DestinationSignerIds":[],"RevokedSourceAccessIds":[],"DestinationAccessIds":[],"TenantLedgerEntryIds":[],"SecurityDepositEntryIds":[],"Error":null}""",
            new TransferLeaseManagementResult(TransferLeaseManagementOutcome.Transferred,
                transfer.TransferPublicId, _scenario.LeaseManagementId, sourceUnitId, 21,
                destinationUnitId, 22, 23, null, 24, businessNow, null, 0m, 0m,
                [], [], [], [], [], [], [], null), "lease-management.transfer-unit.v1");

        var cancelSuccessor = new CancelLeaseAgreementSuccessorDraftCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SuccessorAgreementId,
            "Frozen replay", ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
            _scenario.AccessRevision,
            $"agreement-successor-cancel:70001:10000:20001:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-agreement.successor-draft.cancel",
            $"70001:10000:20001:{FrozenKeyDigest}", cancelSuccessor, SuccessorCancelFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"LeaseAgreementId":20001,"DraftCanceledAtUtc":"2026-08-22T12:00:00Z","DraftCanceledByUserId":790,"DraftCancellationReason":"Frozen replay","Error":null}""",
            new CancelLeaseAgreementSuccessorDraftResult(
                CancelLeaseAgreementSuccessorDraftOutcome.Canceled, _scenario.LeaseManagementId,
                _scenario.SuccessorAgreementId, businessNow, ActorUserId, "Frozen replay", null),
            "lease-agreement.successor-draft.cancel.v1");

        var addendumResult = new LeaseAddendumDraftMutationResult(
            LeaseAddendumDraftMutationOutcome.Applied, _scenario.LeaseManagementId, 31,
            Guid.Parse("33333333-3333-3333-3333-333333333333"), 1, 1, null, [], [], null);
        const string addendumJson = """{"Outcome":0,"LeaseManagementId":10000,"LeaseAddendumId":31,"SeriesPublicId":"33333333-3333-3333-3333-333333333333","VersionNumber":1,"DraftRevision":1,"SourceAddendumId":null,"LeaseAddendumSignerIds":[],"FinancialEffectIds":[],"Error":null}""";
        await AssertFrozenReplayAsync("lease-addendum.draft.create",
            $"70001:10000:{FrozenKeyDigest}",
            new CreateLeaseAddendumDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SourceAgreementId,
                "A-1", LeaseAddendumPurpose.Other, businessDate, null, 1, "{}", 1, [], [],
                ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
                _scenario.AccessRevision, $"addendum-create:70001:10000:{FrozenKeyDigest}"),
            AddendumCreateFingerprint, addendumJson, addendumResult,
            "lease-addendum.draft.mutation.v1");
        await AssertFrozenReplayAsync("lease-addendum.draft.edit",
            $"70001:31:{FrozenKeyDigest}",
            new EditLeaseAddendumDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, 31, 1, "A-1",
                LeaseAddendumPurpose.Other, businessDate, null, 1, "{}", 1, [], [], ActorUserId,
                _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
                $"addendum-edit:70001:31:{FrozenKeyDigest}"), AddendumEditFingerprint,
            addendumJson, addendumResult, "lease-addendum.draft.mutation.v1");
        await AssertFrozenReplayAsync("lease-addendum.draft.correct",
            $"70001:31:{FrozenKeyDigest}",
            new CorrectLeaseAddendumDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, 31, businessDate, ActorUserId,
                _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
                $"addendum-correct:70001:31:{FrozenKeyDigest}"), AddendumCorrectFingerprint,
            addendumJson, addendumResult, "lease-addendum.draft.mutation.v1");

        var agreementResult = new LeaseAgreementDraftMutationResult(
            LeaseAgreementDraftMutationOutcome.Applied, _scenario.LeaseManagementId, 41,
            1, 1, _scenario.SourceAgreementId, [], [], [], null);
        const string agreementJson = """{"Outcome":0,"LeaseManagementId":10000,"LeaseAgreementId":41,"VersionNumber":1,"DraftRevision":1,"SourceAgreementId":20000,"LeaseAgreementSignerIds":[],"AddendumDecisionIds":[],"ReplacementAddendumIds":[],"Error":null}""";
        await AssertFrozenReplayAsync("lease-agreement.draft.edit",
            $"70001:10000:20000:{FrozenKeyDigest}",
            new EditLeaseAgreementDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SourceAgreementId,
                1, "L-1", LeaseAgreementTermType.FixedTerm, businessDate,
                businessDate.AddYears(1), businessDate, 1000m, 1, 500m, 25m, 5, 1, "{}", null,
                [], ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
                _scenario.AccessRevision,
                $"agreement-draft-edit:70001:10000:20000:{FrozenKeyDigest}"),
            AgreementEditFingerprint, agreementJson, agreementResult,
            "lease-agreement.draft.edit.v1");
        await AssertFrozenReplayAsync("lease-agreement.successor-draft.create",
            $"70001:10000:20000:{FrozenKeyDigest}",
            new CreateLeaseAgreementSuccessorDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SourceAgreementId,
                LeaseAgreementChangeType.Renewal, businessDate, businessDate.AddYears(1),
                businessDate, null, null, [], ActorUserId, _scenario.SessionId,
                _scenario.AccessContextId, _scenario.AccessRevision,
                $"agreement-successor:70001:10000:20000:{FrozenKeyDigest}"),
            SuccessorCreateFingerprint, agreementJson, agreementResult,
            "lease-agreement.successor-draft.create.v2");
        await AssertFrozenReplayAsync("lease-agreement.issued-replacement-draft.create",
            $"70001:10000:20011:{FrozenKeyDigest}",
            new ReplaceIssuedAgreementWithDraftCommand(
                _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.IssuedAgreementId,
                null, "Frozen replay", ActorUserId, _scenario.SessionId,
                _scenario.AccessContextId, _scenario.AccessRevision,
                $"agreement-issued-replacement:70001:10000:20011:{FrozenKeyDigest}"),
            IssuedReplacementFingerprint, agreementJson, agreementResult,
            "lease-agreement.successor-draft.create.v2");

        var disposition = new CreatePropertyDispositionCommand(
            _scenario.PortfolioId, propertyId, businessNow.Date, 250_000m, 12_500m,
            "Frozen Buyer", "Frozen replay", ActorUserId, _scenario.SessionId,
            _scenario.AccessContextId, _scenario.AccessRevision,
            $"property-disposition:70001:70002:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("property-disposition.create",
            $"property-disposition:70001:70002:{FrozenKeyDigest}", disposition,
            DispositionFingerprint,
            """{"Outcome":0,"DispositionId":51,"LeaseManagementCount":3,"TenantAccountCount":1}""",
            new CreatePropertyDispositionResult(CreatePropertyDispositionOutcome.Created, 51, 3, 1),
            "property-disposition.create.v1");

        var givePossession = new GivePossessionCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId, ActorUserId,
            _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision, businessNow,
            $"give-possession:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.give-possession",
            $"70001:10000:{FrozenKeyDigest}", givePossession, GivePossessionFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"PossessionGivenAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new GivePossessionResult(GivePossessionOutcome.Given, 10_000, sourceUnitId,
                businessNow, null), "lease-management.give-possession.v1");

        var reconcilePossession = new ReconcileHistoricalPossessionCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId, businessDate,
            ActorUserId, _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
            businessNow, $"reconcile-historical-possession:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.reconcile-historical-possession",
            $"70001:10000:{FrozenKeyDigest}", reconcilePossession,
            ReconcilePossessionFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"PossessionGivenAtUtc":"2026-08-22T00:00:00Z","Error":null}""",
            new ReconcileHistoricalPossessionResult(ReconcileHistoricalPossessionOutcome.Reconciled,
                10_000, sourceUnitId, businessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), null),
            "lease-management.reconcile-historical-possession.v1");

        var confirmMoveIn = new ConfirmMoveInCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId,
            null, null, null, null, ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
            _scenario.AccessRevision, businessNow,
            $"confirm-move-in:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.confirm-move-in",
            $"70001:10000:{FrozenKeyDigest}", confirmMoveIn, ConfirmMoveInFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"PossessionGivenAtUtc":"2026-08-22T12:00:00Z","SecurityDepositEntryId":61,"TenantLedgerEntryId":62,"CompletedAppointmentId":63,"Error":null}""",
            new ConfirmMoveInResult(ConfirmMoveInOutcome.Confirmed, 10_000, sourceUnitId,
                businessNow, 61, 62, 63, null), "lease-management.confirm-move-in.v1");

        var returnPossession = new ReturnPossessionCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, sourceUnitId, ActorUserId,
            _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision, businessNow,
            [new ReturnPossessionParty(1, ReturnPartyDisposition.EndMembership)], [],
            "Frozen replay", $"return-possession:70001:10000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-management.return-possession",
            $"70001:10000:{FrozenKeyDigest}", returnPossession, ReturnPossessionFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"TurnoverPeriodId":51,"PossessionReturnedAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new ReturnPossessionResult(ReturnPossessionOutcome.Returned, 10_000, sourceUnitId,
                51, businessNow, null), "lease-management.return-possession.v1");

        var completeTurnover = new CompleteTurnoverCommand(
            _scenario.PortfolioId, sourceUnitId, 51, ActorUserId, _scenario.SessionId,
            _scenario.AccessContextId, _scenario.AccessRevision, businessNow,
            $"complete-turnover:70001:70003:51:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("unit.complete-turnover",
            $"70001:70003:51:{FrozenKeyDigest}", completeTurnover, CompleteTurnoverFingerprint,
            """{"Outcome":0,"UnitId":70003,"TurnoverPeriodId":51,"CompletedAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new CompleteTurnoverResult(CompleteTurnoverOutcome.Completed, sourceUnitId, 51,
                businessNow, null), "unit.complete-turnover.v1");

        var voidAgreement = new VoidLeaseAgreementCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SourceAgreementId,
            "FROZEN", "Frozen replay", ActorUserId, _scenario.SessionId,
            _scenario.AccessContextId, _scenario.AccessRevision,
            $"agreement-void:70001:20000:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-agreement.void",
            $"70001:20000:{FrozenKeyDigest}", voidAgreement, VoidAgreementFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"LeaseAgreementId":20000,"LeaseAddendumId":null,"VoidedAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new VoidLegalArtifactResult(VoidLegalArtifactOutcome.Voided, 10_000, 20_000,
                null, businessNow, null), "lease-agreement.void.v1");

        var voidAddendum = new VoidLeaseAddendumCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, 31, "FROZEN", "Frozen replay",
            ActorUserId, _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
            $"addendum-void:70001:31:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("lease-addendum.void",
            $"70001:31:{FrozenKeyDigest}", voidAddendum, VoidAddendumFingerprint,
            """{"Outcome":0,"LeaseManagementId":10000,"LeaseAgreementId":null,"LeaseAddendumId":31,"VoidedAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new VoidLegalArtifactResult(VoidLegalArtifactOutcome.Voided, 10_000, null,
                31, businessNow, null), "lease-addendum.void.v1");

        var closeAccount = new CloseTenantAccountCommand(
            _scenario.PortfolioId, 10_001, 40_001, "FROZEN", "Frozen replay", ActorUserId,
            _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision,
            $"tenant-account-close:70001:40001:{FrozenKeyDigest}");
        await AssertFrozenReplayAsync("tenant-account.close",
            $"70001:40001:{FrozenKeyDigest}", closeAccount, CloseAccountFingerprint,
            """{"Outcome":0,"LeaseManagementId":10001,"TenantAccountId":40001,"ClosedAtUtc":"2026-08-22T12:00:00Z","Error":null}""",
            new CloseTenantAccountResult(CloseTenantAccountOutcome.Closed, 10_001, 40_001,
                businessNow, null), "tenant-account.close.v1");
    }

    [SkippableFact]
    public async Task PossessionFrozenLegacyReceiptReplay_RefusesRevokedSession()
    {
        SkipIfNoDocker();
        var command = new GivePossessionCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, 70_003, ActorUserId,
            _scenario.SessionId, _scenario.AccessContextId, _scenario.AccessRevision, FrozenNow,
            $"give-possession:70001:10000:{FrozenKeyDigest}");
        var key = $"70001:10000:{FrozenKeyDigest}";
        await SeedFrozenReceiptAsync("lease-management.give-possession", key,
            GivePossessionFingerprint, "lease-management.give-possession.v1",
            """{"Outcome":0,"LeaseManagementId":10000,"UnitId":70003,"PossessionGivenAtUtc":"2026-08-22T12:00:00Z","Error":null}""");
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(row => row.Id == _scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            await revoke.SaveChangesAsync();
        }

        var write = LeasingWriteSupport.Write<GivePossessionCommand, GivePossessionResult>(
            _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>(), command);
        Func<Task> act = async () => await _scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(key, write);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [SkippableFact]
    public async Task FrozenLegacyReceiptReplay_RefusesRevokedSession()
    {
        SkipIfNoDocker();
        var command = new CancelLeaseAgreementSuccessorDraftCommand(
            _scenario.PortfolioId, _scenario.LeaseManagementId, _scenario.SuccessorAgreementId,
            "Frozen replay", ActorUserId, _scenario.SessionId, _scenario.AccessContextId,
            _scenario.AccessRevision,
            $"agreement-successor-cancel:70001:10000:20001:{FrozenKeyDigest}");
        var key = $"70001:10000:20001:{FrozenKeyDigest}";
        await SeedFrozenReceiptAsync("lease-agreement.successor-draft.cancel", key,
            SuccessorCancelFingerprint, "lease-agreement.successor-draft.cancel.v1",
            """{"Outcome":0,"LeaseManagementId":10000,"LeaseAgreementId":20001,"DraftCanceledAtUtc":"2026-08-22T12:00:00Z","DraftCanceledByUserId":790,"DraftCancellationReason":"Frozen replay","Error":null}""");
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(row => row.Id == _scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            await revoke.SaveChangesAsync();
        }

        var write = LeasingWriteSupport.Write<CancelLeaseAgreementSuccessorDraftCommand,
            CancelLeaseAgreementSuccessorDraftResult>(
            _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>(), command);
        Func<Task> act = async () => await _scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(key, write);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [SkippableFact]
    public async Task Issued_and_executed_successors_are_rejected_without_cancellation_facts()
    {
        SkipIfNoDocker();
        await AssertIssuedOrExecutedRejectedAsync(_scenario.IssuedAgreementId, "issued");
        await AssertIssuedOrExecutedRejectedAsync(_scenario.ExecutedAgreementId, "executed");

        await using var db = NewContext();
        var protectedRows = await db.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.Id == _scenario.IssuedAgreementId
                || agreement.Id == _scenario.ExecutedAgreementId)
            .OrderBy(agreement => agreement.Id)
            .Select(agreement => new
            {
                agreement.DraftCanceledAtUtc,
                agreement.DraftCanceledByUserId,
                agreement.DraftCancellationReason,
            })
            .ToListAsync();
        protectedRows.Should().OnlyContain(row => row.DraftCanceledAtUtc == null
            && row.DraftCanceledByUserId == null && row.DraftCancellationReason == null);
    }

    [SkippableFact]
    public async Task Cancel_rejects_revoked_session_without_reopening_successor_slot()
    {
        SkipIfNoDocker();
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(
                candidate => candidate.Id == _scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevocationReason = "Authorization race contract test.";
            await revoke.SaveChangesAsync();
        }

        var command = Command("revoked-session", "This mutation must not commit.");
        Func<Task> act = async () => await ExecuteCancelAsync(command.DeliveryIdempotencyKey, command);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        await using var assert = NewContext();
        var successor = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.SuccessorAgreementId);
        successor.DraftCanceledAtUtc.Should().BeNull();
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "lease-agreement.successor-draft.cancel"
            && receipt.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Issued_replacement_rejects_revoked_session_without_any_mutations()
    {
        SkipIfNoDocker();
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(
                candidate => candidate.Id == _scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevocationReason = "Authorization race contract test.";
            await revoke.SaveChangesAsync();
        }

        await AssertRecoveryDeniedAsync(RecoveryCommand("revoked-session"));
    }

    [SkippableFact]
    public async Task Issued_replacement_rejects_stale_access_revision_without_any_mutations()
    {
        SkipIfNoDocker();
        await using (var advance = NewContext())
        {
            var accessContext = await advance.WorkspaceAccessContexts.SingleAsync(
                candidate => candidate.Id == _scenario.AccessContextId);
            accessContext.AdvanceRevision(_scenario.AccessRevision);
            await advance.SaveChangesAsync();
        }

        await AssertRecoveryDeniedAsync(RecoveryCommand("stale-access-revision"));
    }

    [SkippableFact]
    public async Task Issued_replacement_rejects_wrong_portfolio_scope_without_any_mutations()
    {
        SkipIfNoDocker();
        await AssertRecoveryDeniedAsync(RecoveryCommand("wrong-portfolio") with
        {
            PortfolioId = _scenario.PortfolioId + 1,
        });
    }

    [SkippableFact]
    public async Task Issued_replacement_rejects_wrong_selected_property_scope_without_any_mutations()
    {
        SkipIfNoDocker();
        await using (var arrange = NewContext())
        {
            var relationship = await arrange.LeaseManagements.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == _scenario.LeaseManagementId + 1);
            var decoyProperty = new Property
            {
                PortfolioId = _scenario.PortfolioId,
                Name = "Out-of-scope property",
                AddressLine1 = "200 Other Ave",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            arrange.Properties.Add(decoyProperty);
            await arrange.SaveChangesAsync();

            var assignment = await arrange.MembershipRoleAssignments
                .SingleAsync(candidate => candidate.WorkspaceMembership!.AccessContextId
                    == _scenario.AccessContextId);
            assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
            arrange.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignmentId = assignment.Id,
                PropertyId = decoyProperty.Id,
                PortfolioId = _scenario.PortfolioId,
            });
            await arrange.SaveChangesAsync();

            relationship.PropertyId.Should().NotBe(decoyProperty.Id);
        }

        await AssertRecoveryDeniedAsync(RecoveryCommand("wrong-selected-property"));
    }

    [SkippableFact]
    public async Task Issued_replacement_rejects_missing_management_and_agreement_prepare_capabilities_without_any_mutations()
    {
        SkipIfNoDocker();
        await using (var arrange = NewContext())
        {
            var assignment = await arrange.MembershipRoleAssignments
                .SingleAsync(candidate => candidate.WorkspaceMembership!.AccessContextId
                    == _scenario.AccessContextId);
            assignment.RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.OwnerPortal).Id;
            await arrange.SaveChangesAsync();
        }

        await AssertRecoveryDeniedAsync(RecoveryCommand("missing-management-capabilities"));
    }

    [SkippableFact]
    public async Task Voided_issued_replacement_is_atomic_replayable_and_preserves_history()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        int predecessorId;
        SourceHistorySnapshot sourceHistory;
        int tenantAccountId;
        await using (var arrange = NewContext())
        {
            var issued = await arrange.LeaseAgreements.SingleAsync(
                agreement => agreement.Id == _scenario.IssuedAgreementId);
            var predecessor = await arrange.LeaseAgreements.SingleAsync(
                agreement => agreement.Id == issued.ReplacesAgreementId);
            predecessorId = predecessor.Id;
            var issuedArtifact = await arrange.LegalDocumentArtifacts.AsNoTracking()
                .SingleAsync(artifact => artifact.Id == issued.IssuedArtifactId);
            sourceHistory = new SourceHistorySnapshot(
                issued.IssuedArtifactId!.Value,
                issuedArtifact.ContentSha256,
                issuedArtifact.LegalIssuanceFingerprint);
            tenantAccountId = await arrange.LeaseManagements.AsNoTracking()
                .Where(management => management.Id == leaseManagementId)
                .Select(management => management.TenantAccount!.Id)
                .SingleAsync();
            var predecessorArtifactId = await AddArtifactAsync(
                arrange, _scenario.PortfolioId, predecessor.Id, 30_003, DateTime.UtcNow);
            predecessor.IssuedArtifactId = predecessorArtifactId;
            predecessor.ExecutedArtifactId = predecessorArtifactId;
            predecessor.IssuedAtUtc = DateTime.UtcNow;
            predecessor.FullyExecutedAtUtc = DateTime.UtcNow;
            arrange.LeaseAgreementSigners.Add(RequiredTenantSigner(
                _scenario.PortfolioId,
                predecessor.Id,
                "executed-predecessor-signer@example.test"));
            issued.VoidedAtUtc = DateTime.UtcNow;
            issued.VoidReasonCode = "ISSUED_AGREEMENT_REPLACED";
            issued.VoidNote = "Incorrect resident legal name.";
            await arrange.SaveChangesAsync();
        }

        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            null,
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:atomic-replay");
        var identity = new AtomicCommandIdentity(
            RecoveryCommandType,
            command.DeliveryIdempotencyKey);

        var first = await ExecuteRecoveryAsync(identity.IdempotencyKey, command);
        var replay = await ExecuteRecoveryAsync(identity.IdempotencyKey, command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        first.Value.SourceAgreementId.Should().Be(_scenario.IssuedAgreementId);

        await AssertSoleGoverningAgreementAsync(leaseManagementId, predecessorId);
        var issuance = await IssueAgreementAsync(
            leaseManagementId,
            first.Value.LeaseAgreementId,
            "issued-replacement:correction-issue");

        await AssertSoleGoverningAgreementAsync(leaseManagementId, predecessorId);

        var execution = await ExecuteAgreementTransitionAsync(
            leaseManagementId,
            first.Value.LeaseAgreementId,
            issuance.IssuedArtifactId,
            DateTime.UtcNow);
        execution.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        execution.PredecessorId.Should().Be(predecessorId);

        await using var assert = NewContext();
        var source = await assert.LeaseAgreements.AsNoTracking()
            .Include(agreement => agreement.IssuedArtifact)
            .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
        var replacement = await assert.LeaseAgreements.AsNoTracking()
            .Include(agreement => agreement.Signers)
            .SingleAsync(agreement => agreement.Id == first.Value.LeaseAgreementId);
        source.IssuedArtifactId.Should().NotBeNull();
        source.VoidedAtUtc.Should().NotBeNull();
        source.VoidReasonCode.Should().Be("ISSUED_AGREEMENT_REPLACED");
        source.VoidNote.Should().Be("Incorrect resident legal name.");
        source.IssuedArtifactId.Should().Be(sourceHistory.IssuedArtifactId);
        source.IssuedArtifact.Should().NotBeNull();
        source.IssuedArtifact!.ContentSha256.Should().Be(sourceHistory.ContentSha256);
        source.IssuedArtifact.LegalIssuanceFingerprint.Should()
            .Be(sourceHistory.LegalIssuanceFingerprint);
        replacement.LeaseManagementId.Should().Be(leaseManagementId);
        replacement.VersionNumber.Should().Be(3);
        replacement.ChangeType.Should().Be(source.ChangeType);
        replacement.CorrectionReason.Should().Be(source.CorrectionReason);
        replacement.ReplacesAgreementId.Should().Be(predecessorId);
        replacement.ReissuesAgreementId.Should().Be(source.Id);
        replacement.ReissueReason.Should().Be("Correct the resident legal name before reissuing.");
        replacement.IssuedArtifactId.Should().Be(issuance.IssuedArtifactId);
        replacement.ExecutedArtifactId.Should().Be(issuance.IssuedArtifactId);
        replacement.FullyExecutedAtUtc.Should().NotBeNull();
        replacement.Signers.Should().ContainSingle();
        replacement.Signers[0].EmailSnapshot.Should().Be("issued-signer@example.test");
		var persistedPredecessor = await assert.LeaseAgreements.AsNoTracking()
			.SingleAsync(agreement => agreement.Id == predecessorId);
		persistedPredecessor.SupersededByAgreementId.Should().Be(replacement.Id);
        var persistedManagement = await assert.LeaseManagements.AsNoTracking()
            .Include(management => management.TenantAccount)
            .SingleAsync(management => management.Id == leaseManagementId);
        persistedManagement.TenantAccount.Should().NotBeNull();
        persistedManagement.TenantAccount!.Id.Should().Be(tenantAccountId);
        (await assert.LeaseAgreementStatusProjections.CountAsync(status =>
            status.PortfolioId == _scenario.PortfolioId
            && status.LeaseManagementId == leaseManagementId
            && status.IsGoverning)).Should().Be(1);
        await AssertSoleGoverningAgreementAsync(leaseManagementId, replacement.Id);
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await assert.AtomicAuditLogs.CountAsync(log =>
            log.CommandType == identity.CommandType
            && log.CommandIdempotencyKey == identity.IdempotencyKey
            && log.EntityType == nameof(LeaseAgreement)
            && log.EntityId == replacement.Id)).Should().BeGreaterThan(0);

        var recoveryOutbox = await assert.OutboxMessages.AsNoTracking()
            .Where(message => message.IdempotencyKey == identity.IdempotencyKey)
            .ToListAsync();
        recoveryOutbox.Should().ContainSingle();
        using var recoveryPayload = JsonDocument.Parse(recoveryOutbox[0].Payload);
        recoveryPayload.RootElement.GetProperty("entityId").GetInt32()
            .Should().Be(replacement.Id);
        var recoveryData = recoveryPayload.RootElement.GetProperty("data");
        recoveryData.GetProperty("mutation").GetString()
            .Should().Be("issued-agreement-replaced-with-draft");
        recoveryData.GetProperty("LeaseManagementId").GetInt32()
            .Should().Be(leaseManagementId);
        recoveryOutbox[0].IdempotencyKey.Should().Be(identity.IdempotencyKey);
    }

    [SkippableFact]
    public async Task Issued_replacement_failure_rolls_back_void_and_draft_together()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        await using (var arrange = NewContext())
        {
            await arrange.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION fail_issued_replacement_signer_copy()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."LeaseAgreementId" <> {_scenario.IssuedAgreementId}
                       AND NEW."EmailSnapshot" = 'issued-signer@example.test' THEN
                        RAISE EXCEPTION 'injected signer copy failure';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER fail_issued_replacement_signer_copy
                BEFORE INSERT ON "LeaseAgreementSigners"
                FOR EACH ROW EXECUTE FUNCTION fail_issued_replacement_signer_copy();
                """);
        }
        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            "Incorrect resident legal name.",
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:rollback");
        var identity = new AtomicCommandIdentity(
            "lease-agreement.issued-replacement-draft.create",
            command.DeliveryIdempotencyKey);

        Func<Task> act = async () => await ExecuteRecoveryAsync(identity.IdempotencyKey, command);
        await act.Should().ThrowAsync<Exception>();

        await using var assert = NewContext();
        var source = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
        source.VoidedAtUtc.Should().BeNull();
        source.VoidReasonCode.Should().BeNull();
        source.VoidNote.Should().BeNull();
        (await assert.LeaseAgreements.CountAsync(agreement =>
            agreement.ReissuesAgreementId == source.Id)).Should().Be(0);
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Canceled_reissue_draft_releases_live_reissue_slot_for_retry()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        var firstCommand = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            "The issued copy must be replaced.",
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:cancel-retry:first");
        var first = await ExecuteRecoveryAsync(firstCommand.DeliveryIdempotencyKey, firstCommand);

        var cancel = new CancelLeaseAgreementSuccessorDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            first.Value.LeaseAgreementId,
            "Abandon this recovery draft and try again.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "successor-cancel:reissue-retry");
        var canceled = await ExecuteCancelAsync(cancel.DeliveryIdempotencyKey, cancel);

        var retryCommand = firstCommand with
        {
            ReissueReason = "Correct the resident legal name using the verified spelling.",
            DeliveryIdempotencyKey = "issued-replacement:cancel-retry:second",
        };
        var retry = await ExecuteRecoveryAsync(retryCommand.DeliveryIdempotencyKey, retryCommand);

        first.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        canceled.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.Canceled);
        retry.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        retry.Value.LeaseAgreementId.Should().NotBe(first.Value.LeaseAgreementId);

        await using var assert = NewContext();
        var reissues = await assert.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.ReissuesAgreementId == _scenario.IssuedAgreementId)
            .OrderBy(agreement => agreement.Id)
            .Select(agreement => new
            {
                agreement.Id,
                agreement.DraftCanceledAtUtc,
                agreement.ReissueReason,
            })
            .ToListAsync();
        reissues.Should().HaveCount(2);
        reissues.Should().ContainSingle(agreement => agreement.DraftCanceledAtUtc != null);
        reissues.Should().ContainSingle(agreement => agreement.DraftCanceledAtUtc == null
            && agreement.ReissueReason == retryCommand.ReissueReason);
    }

    [SkippableFact]
    public async Task Reissued_initial_executes_without_inventing_a_predecessor()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 10;
        const int sourceAgreementId = 20_030;
        await SeedIssuedInitialAsync(leaseManagementId, sourceAgreementId, 30_005);

        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            sourceAgreementId,
            "The issued copy contains a clerical error.",
            "Reissue the initial Agreement with the verified resident details.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:initial-execute");
        var recovery = await ExecuteRecoveryAsync(command.DeliveryIdempotencyKey, command);

        recovery.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        await using (var inspect = NewContext())
        {
            var replacement = await inspect.LeaseAgreements.AsNoTracking().SingleAsync(
                agreement => agreement.Id == recovery.Value.LeaseAgreementId);
            replacement.ChangeType.Should().Be(LeaseAgreementChangeType.Initial);
            replacement.VersionNumber.Should().Be(2);
            replacement.ReplacesAgreementId.Should().BeNull();
            replacement.RenewsAgreementId.Should().BeNull();
            replacement.ReissuesAgreementId.Should().Be(sourceAgreementId);
        }
        var issuance = await IssueAgreementAsync(
            leaseManagementId,
            recovery.Value.LeaseAgreementId,
            "issued-replacement:initial-issue");

        var execution = await ExecuteAgreementTransitionAsync(
            leaseManagementId,
            recovery.Value.LeaseAgreementId,
            issuance.IssuedArtifactId,
            DateTime.UtcNow);
        execution.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        execution.PredecessorId.Should().BeNull();

        await using var assert = NewContext();
        var rows = await assert.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.LeaseManagementId == leaseManagementId)
            .OrderBy(agreement => agreement.VersionNumber)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].VoidedAtUtc.Should().NotBeNull();
        rows[0].SupersededByAgreementId.Should().BeNull();
        rows[1].ExecutedArtifactId.Should().Be(issuance.IssuedArtifactId);
        rows[1].ReissuesAgreementId.Should().Be(sourceAgreementId);
        rows[1].ReplacesAgreementId.Should().BeNull();
        rows[1].RenewsAgreementId.Should().BeNull();
    }

    private async Task AssertIssuedOrExecutedRejectedAsync(int agreementId, string suffix)
    {
        var command = Command($"reject-{suffix}", $"Do not cancel the {suffix} Agreement.") with
        {
            LeaseManagementId = _scenario.LeaseManagementId
                + (agreementId == _scenario.IssuedAgreementId ? 1 : 2),
            LeaseAgreementId = agreementId,
        };
        var outcome = await ExecuteCancelAsync(command.DeliveryIdempotencyKey, command);

        outcome.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.IssuedOrExecuted);
    }

    private async Task AssertRecoveryDeniedAsync(ReplaceIssuedAgreementWithDraftCommand command)
    {
        var identity = new AtomicCommandIdentity(RecoveryCommandType, command.DeliveryIdempotencyKey);
        int agreementCountBefore;
        int signerCountBefore;
        LeaseAgreement sourceBefore;
        await using (var before = NewContext())
        {
            sourceBefore = await before.LeaseAgreements.AsNoTracking()
                .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
            agreementCountBefore = await before.LeaseAgreements.CountAsync(
                agreement => agreement.LeaseManagementId == command.LeaseManagementId);
            signerCountBefore = await before.LeaseAgreementSigners.CountAsync(signer =>
                signer.LeaseAgreement!.LeaseManagementId == command.LeaseManagementId);
        }

        Func<Task> act = async () => await ExecuteRecoveryAsync(identity.IdempotencyKey, command);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        await using var assert = NewContext();
        var sourceAfter = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
        sourceAfter.VoidedAtUtc.Should().Be(sourceBefore.VoidedAtUtc);
        sourceAfter.VoidReasonCode.Should().Be(sourceBefore.VoidReasonCode);
        sourceAfter.VoidNote.Should().Be(sourceBefore.VoidNote);
        (await assert.LeaseAgreements.CountAsync(
            agreement => agreement.LeaseManagementId == command.LeaseManagementId))
            .Should().Be(agreementCountBefore);
        (await assert.LeaseAgreements.CountAsync(agreement =>
            agreement.LeaseManagementId == command.LeaseManagementId
            && agreement.ReissuesAgreementId == command.SourceAgreementId)).Should().Be(0);
        (await assert.LeaseAgreementSigners.CountAsync(signer =>
            signer.LeaseAgreement!.LeaseManagementId == command.LeaseManagementId))
            .Should().Be(signerCountBefore);
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await assert.AtomicAuditLogs.CountAsync(log =>
            log.CommandType == identity.CommandType
            && log.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await assert.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private async Task AssertSoleGoverningAgreementAsync(
        int leaseManagementId,
        int expectedAgreementId)
    {
        await using var db = NewContext();
        var governingAgreementIds = await db.LeaseAgreementStatusProjections.AsNoTracking()
            .Where(status => status.PortfolioId == _scenario.PortfolioId
                && status.LeaseManagementId == leaseManagementId
                && status.IsGoverning)
            .OrderBy(status => status.AgreementId)
            .Select(status => status.AgreementId)
            .ToListAsync();
        governingAgreementIds.Should().Equal(new[] { expectedAgreementId });
    }

    private ReplaceIssuedAgreementWithDraftCommand RecoveryCommand(
        string key,
        int? portfolioId = null,
        long? accessRevision = null) => new(
        portfolioId ?? _scenario.PortfolioId,
        _scenario.LeaseManagementId + 1,
        _scenario.IssuedAgreementId,
        "The issued copy must be replaced.",
        "Correct the resident legal name before reissuing.",
        ActorUserId,
        _scenario.SessionId,
        _scenario.AccessContextId,
        accessRevision ?? _scenario.AccessRevision,
        $"issued-replacement:authorization:{key}");

    private CancelLeaseAgreementSuccessorDraftCommand Command(string key, string reason) => new(
        _scenario.PortfolioId,
        _scenario.LeaseManagementId,
        _scenario.SuccessorAgreementId,
        reason,
        ActorUserId,
        _scenario.SessionId,
        _scenario.AccessContextId,
        _scenario.AccessRevision,
        $"successor-cancel:{key}");

    private Task<AtomicCommandOutcome<CancelLeaseAgreementSuccessorDraftResult>> ExecuteCancelAsync(
        string key,
        CancelLeaseAgreementSuccessorDraftCommand command)
    {
        var db = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return _scope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            key, LeasingWriteSupport.Write<CancelLeaseAgreementSuccessorDraftCommand,
                CancelLeaseAgreementSuccessorDraftResult>(db, command));
    }

    private Task<AtomicCommandOutcome<RecordLeaseEndingDispositionResult>> ExecuteEndingAsync(
        string key,
        RecordLeaseEndingDispositionCommand command)
    {
        var db = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return _scope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            key, LeasingWriteSupport.Write<RecordLeaseEndingDispositionCommand,
                RecordLeaseEndingDispositionResult>(db, command));
    }

    private async Task<EndingCounts> EndingCountsAsync(string key, string deliveryKey)
    {
        await using var db = NewContext();
        return new(
            await db.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == "lease-management.ending-disposition" && row.IdempotencyKey == key),
            await db.AtomicAuditLogs.CountAsync(row => row.EntityType == nameof(LeaseManagement)
                && row.EntityId == _scenario.LeaseManagementId),
            await db.OutboxMessages.CountAsync(row => row.IdempotencyKey == deliveryKey));
    }

    private Task<AtomicCommandOutcome<CreatePropertyDispositionResult>> ExecutePropertyDispositionAsync(
        string key,
        CreatePropertyDispositionCommand command)
    {
        var db = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return _scope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            key, LeasingWriteSupport.Write<CreatePropertyDispositionCommand,
                CreatePropertyDispositionResult>(db, command));
    }

    private async Task AssertFrozenReplayAsync<TCommand, TResult>(
        string operation,
        string key,
        TCommand command,
        string legacyFingerprint,
        string legacyResultJson,
        TResult storedResult,
        string resultContract)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var scopedDb = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = LeasingWriteSupport.Write<TCommand, TResult>(scopedDb, command);
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(resultContract);
        await SeedFrozenReceiptAsync(
            operation, key, legacyFingerprint, resultContract, legacyResultJson);

        int auditsBefore;
        int outboxBefore;
        await using (var before = NewContext())
        {
            auditsBefore = await before.AtomicAuditLogs.CountAsync();
            outboxBefore = await before.OutboxMessages.CountAsync();
        }
        var replay = await _scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(key, write);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(storedResult);
        await using var verify = NewContext();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == operation && row.IdempotencyKey == key)).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync()).Should().Be(auditsBefore);
        (await verify.OutboxMessages.CountAsync()).Should().Be(outboxBefore);
    }

    private async Task SeedFrozenReceiptAsync(
        string operation,
        string key,
        string legacyFingerprint,
        string resultContract,
        string legacyResultJson)
    {
        await using (var seed = NewContext())
        {
            seed.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = operation,
                IdempotencyKey = key,
                RequestFingerprint = legacyFingerprint,
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = resultContract,
                ResultJson = legacyResultJson,
                StartedAt = FrozenNow,
                CompletedAt = FrozenNow,
            });
            await seed.SaveChangesAsync();
        }
    }

    private async Task<PropertyDispositionCounts> PropertyDispositionCountsAsync(
        int propertyId,
        string deliveryKey)
    {
        await using var db = NewContext();
        return new(
            await db.PropertyDispositions.CountAsync(row => row.PropertyId == propertyId),
            await db.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == "property-disposition.create" && row.IdempotencyKey == deliveryKey),
            await db.AtomicAuditLogs.CountAsync(row => row.PortfolioId == _scenario.PortfolioId),
            await db.OutboxMessages.CountAsync(row => row.IdempotencyKey == deliveryKey));
    }

    private Task<AtomicCommandOutcome<LeaseAgreementDraftMutationResult>> ExecuteRecoveryAsync(
        string key,
        ReplaceIssuedAgreementWithDraftCommand command)
    {
        var db = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return _scope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            key, LeasingWriteSupport.Write<ReplaceIssuedAgreementWithDraftCommand,
                LeaseAgreementDraftMutationResult>(db, command));
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private async Task<IssueLeaseAgreementResult> IssueAgreementAsync(
        int leaseManagementId,
        int leaseAgreementId,
        string operationKey)
    {
        LeaseAgreement draft;
        await using (var arrange = NewContext())
        {
            draft = await arrange.LeaseAgreements.AsNoTracking()
                .Include(agreement => agreement.Signers)
                .SingleAsync(agreement => agreement.Id == leaseAgreementId);
            var contentSha256 = new string('c', 64);
            var fileName = $"agreement-{leaseAgreementId}.pdf";
            var storageKey = $"agreements/{leaseAgreementId}/{operationKey}.pdf";
            var fingerprint = LegalDocumentIssuanceBinding.Create(
                nameof(LeaseAgreement),
                _scenario.PortfolioId,
                leaseManagementId,
                leaseAgreementId,
                draft.DraftRevision,
                draft.DocumentSourceVersionId,
                draft.TermsSchemaVersion,
                draft.TermsPayload,
                contentSha256,
                1,
                fileName);
            var pendingUploadId = Guid.NewGuid();
            arrange.PendingFileUploads.Add(new PendingFileUpload
            {
                Id = pendingUploadId,
                PortfolioId = _scenario.PortfolioId,
                ActorScopeId = ActorUserId,
                Purpose = LegalDocumentIssuanceBinding.AgreementUploadPurpose,
                OperationKeyHash = new string('d', 64),
                RequestFingerprint = fingerprint,
                StoragePath = storageKey,
                FileName = fileName,
                ContentType = "application/pdf",
                SizeBytes = 1,
                State = PendingFileUploadState.Prepared,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await arrange.SaveChangesAsync();

            var command = new IssueLeaseAgreementCommand(
                pendingUploadId,
                draft.DocumentSourceVersionId,
                fingerprint,
                _scenario.PortfolioId,
                leaseManagementId,
                leaseAgreementId,
                draft.DraftRevision,
                operationKey,
                $"Agreement {draft.AgreementNumber}",
                storageKey,
                fileName,
                1,
                contentSha256,
                "https://rental-command.example.test",
                draft.Signers.OrderBy(signer => signer.Id)
                    .Select(signer => new NativeEsignSignerCommand(signer.Id))
                    .ToArray(),
                ActorUserId,
                _scenario.SessionId,
                _scenario.AccessContextId,
                _scenario.AccessRevision);
            var db = _scope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var issued = await _scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
                .ExecuteAsync(operationKey,
                    NativeEsignWriteSupport.Write<IssueLeaseAgreementCommand,
                        IssueLeaseAgreementResult>(db, command));
            var replay = await _scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
                .ExecuteAsync(operationKey,
                    NativeEsignWriteSupport.Write<IssueLeaseAgreementCommand,
                        IssueLeaseAgreementResult>(db, command));
            issued.Disposition.Should().Be(AtomicCommandDisposition.Executed);
            replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
            replay.Value.Should().BeEquivalentTo(issued.Value);
            var legacyKey = $"{operationKey}-legacy-receipt";
            var issueCodec = new AtomicJsonResultCodec<IssueLeaseAgreementResult>("lease-agreement.issue.v1");
            await using (var receiptDb = NewContext())
            {
                receiptDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
                {
                    Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = "lease-agreement.issue",
                    IdempotencyKey = legacyKey, RequestFingerprint = AtomicCommandFingerprint.Create(command),
                    Status = AtomicCommandReceiptStatus.Completed, ResultContract = issueCodec.ContractName,
                    ResultJson = issueCodec.Serialize(issued.Value), StartedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                });
                await receiptDb.SaveChangesAsync();
            }
            var legacyReplay = await _scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
                .ExecuteAsync(legacyKey,
                    NativeEsignWriteSupport.Write<IssueLeaseAgreementCommand,
                        IssueLeaseAgreementResult>(db, command));
            legacyReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
            legacyReplay.Value.Should().BeEquivalentTo(issued.Value);
            return issued.Value;
        }
    }

    private async Task<AtomicLegalExecutionTransitionResult> ExecuteAgreementTransitionAsync(
        int leaseManagementId,
        int leaseAgreementId,
        int executedArtifactId,
        DateTime executedAtUtc)
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var auditScope = new AtomicAuditScope(TimeProvider.System);
        var attemptId = Guid.NewGuid();
        var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
        commandContext.BeginAttempt(attemptId);
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity(
                "test.issued-agreement-reissue-transition",
                Guid.NewGuid().ToString("N")),
            attemptId,
            db);

        var result = await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
            db,
            commandContext,
            _scenario.PortfolioId,
            leaseManagementId,
            leaseAgreementId,
            null,
            executedArtifactId,
            executedAtUtc);
        await transaction.CommitAsync();
        commandContext.EndAttempt();
        return result;
    }

    private async Task SeedIssuedInitialAsync(
        int leaseManagementId,
        int sourceAgreementId,
        int issuedArtifactId)
    {
        await using var db = NewContext();
        var existingRelationship = await db.LeaseManagements.AsNoTracking()
            .SingleAsync(relationship => relationship.Id == _scenario.LeaseManagementId);
        var sourceVersionId = await db.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.Id == _scenario.SourceAgreementId)
            .Select(agreement => agreement.DocumentSourceVersionId)
            .SingleAsync();
        var now = DateTime.UtcNow;
        var relationship = new LeaseManagement
        {
            Id = leaseManagementId,
            PublicId = Guid.NewGuid(),
            PortfolioId = _scenario.PortfolioId,
            PropertyId = existingRelationship.PropertyId,
            UnitId = existingRelationship.UnitId,
            RelationshipNumber = "REL-CANCEL-INITIAL-REISSUE",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            RowVersion = Guid.NewGuid(),
        };
        var source = NewAgreement(
            sourceAgreementId,
            relationship,
            sourceVersionId,
            1,
            LeaseAgreementChangeType.Initial,
            now);
        db.AddRange(relationship, source);
        db.LeaseAgreementSigners.Add(RequiredTenantSigner(
            _scenario.PortfolioId,
            sourceAgreementId,
            "initial-issued-signer@example.test"));
        await db.SaveChangesAsync();

        source.IssuedArtifactId = await AddArtifactAsync(
            db, _scenario.PortfolioId, sourceAgreementId, issuedArtifactId, now);
        source.IssuedAtUtc = now;
        await db.SaveChangesAsync();
    }

    private async Task<Scenario> SeedAsync(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "successor-cancel-user",
            NormalizedUserName = "SUCCESSOR-CANCEL-USER",
            Email = "successor-cancel@example.test",
            NormalizedEmail = "SUCCESSOR-CANCEL@EXAMPLE.TEST",
            DisplayName = "Successor Cancel User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Id = 70_001,
            Name = "Successor Cancel Portfolio",
            ManagementCompanyName = "Successor Cancel Management",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Id = 70_002,
            Portfolio = portfolio,
            Name = "Successor Cancel Property",
            AddressLine1 = "100 Test Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Id = 70_003,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            Portfolio = portfolio,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Successor cancel lease",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var sourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = "successor-cancel-template:v1",
            DocumentTemplate = template,
            DocumentTemplateVersion = 1,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };

        var relationships = Enumerable.Range(0, 3).Select(index => new LeaseManagement
        {
            Id = 10_000 + index,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"REL-CANCEL-{index + 1}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            RowVersion = Guid.NewGuid(),
        }).ToArray();

        db.AddRange(user, portfolio, property, unit, template, sourceVersion);
        db.AddRange(relationships);
        await db.SaveChangesAsync();

        db.TenantAccounts.Add(new TenantAccount
        {
            Id = 40_001,
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationships[1].Id,
            AccountNumber = "AR-CANCEL-1001",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        });
        await db.SaveChangesAsync();

        var sourceIds = new[] { 20_000, 20_010, 20_020 };
        var successorIds = new[] { 20_001, 20_011, 20_021 };
        for (var index = 0; index < relationships.Length; index++)
        {
            db.LeaseAgreements.Add(NewAgreement(
                sourceIds[index], relationships[index], sourceVersion.Id, 1,
                LeaseAgreementChangeType.Initial, now));
            db.LeaseAgreements.Add(NewAgreement(
                successorIds[index], relationships[index], sourceVersion.Id, 2,
                LeaseAgreementChangeType.Correction, now, sourceIds[index]));
        }
        await db.SaveChangesAsync();

        var issuedArtifactId = await AddArtifactAsync(db, portfolio.Id, successorIds[1], 30_001, now);
        var executedArtifactId = await AddArtifactAsync(db, portfolio.Id, successorIds[2], 30_002, now);
        var issued = await db.LeaseAgreements.SingleAsync(agreement => agreement.Id == successorIds[1]);
        issued.GoverningFromOn = new DateOnly(2026, 6, 1);
        issued.IssuedArtifactId = issuedArtifactId;
        issued.IssuedAtUtc = now;
        var executed = await db.LeaseAgreements.SingleAsync(agreement => agreement.Id == successorIds[2]);
        executed.IssuedArtifactId = executedArtifactId;
        executed.IssuedAtUtc = now;
        executed.ExecutedArtifactId = executedArtifactId;
        executed.FullyExecutedAtUtc = now;
        executed.GoverningFromOn = new DateOnly(2026, 2, 1);
        var executedPredecessor = await db.LeaseAgreements.SingleAsync(
            agreement => agreement.Id == sourceIds[2]);
        executedPredecessor.SupersededEffectiveOn = executed.GoverningFromOn;
        executedPredecessor.SupersededByAgreementId = executed.Id;
        executedPredecessor.SupersessionRecordedAtUtc = now;
        db.LeaseAgreementSigners.AddRange(
            RequiredTenantSigner(portfolio.Id, successorIds[1], "issued-signer@example.test"),
            RequiredTenantSigner(portfolio.Id, successorIds[2], "executed-signer@example.test"));

        var accessContext = new WorkspaceAccessContext
        {
            Id = 70_006,
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
            Id = FrozenSessionId,
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        return new Scenario(
            portfolio.Id,
            relationships[0].Id,
            sourceIds[0],
            successorIds[0],
            20_002,
            successorIds[1],
            successorIds[2],
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static LeaseAgreementSigner RequiredTenantSigner(
        int portfolioId,
        int leaseAgreementId,
        string email) => new()
        {
            PortfolioId = portfolioId,
            LeaseAgreementId = leaseAgreementId,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Test Tenant",
            EmailSnapshot = email,
            SigningOrder = 1,
            IsRequired = true,
        };

    private LeaseAgreement NewSuccessor(
        int id,
        int version,
        LeaseAgreementChangeType changeType,
        string? correctionReason) => NewAgreement(
        id,
        new LeaseManagement
        {
            Id = _scenario.LeaseManagementId,
            PortfolioId = _scenario.PortfolioId,
        },
        documentSourceVersionId: 1,
        version,
        changeType,
        DateTime.UtcNow,
        _scenario.SourceAgreementId,
        correctionReason);

    private static LeaseAgreement NewAgreement(
        int id,
        LeaseManagement relationship,
        int documentSourceVersionId,
        int version,
        LeaseAgreementChangeType changeType,
        DateTime now,
        int? sourceAgreementId = null,
        string? correctionReason = "Correct the resident name.") => new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = relationship.PortfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = version,
            AgreementNumber = $"AGR-{relationship.Id}-V{version}",
            ChangeType = changeType,
            CorrectionReason = changeType == LeaseAgreementChangeType.Correction ? correctionReason : null,
            ReplacesAgreementId = sourceAgreementId,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2026, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1),
            BaseRentAmount = 1_000,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };

    private static async Task<int> AddArtifactAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int agreementId,
        int id,
        DateTime now)
    {
        var file = new StoredFile
        {
            Id = id,
            PortfolioId = portfolioId,
            FileName = $"agreement-{agreementId}.pdf",
            FilePath = $"agreements/{agreementId}.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            EntityType = nameof(LeaseAgreement),
            EntityId = agreementId,
            UploadedAt = now,
        };
        var artifact = new LegalDocumentArtifact
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            StoredFile = file,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = 1,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        db.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; successor cancellation proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record SourceHistorySnapshot(
        int IssuedArtifactId,
        string ContentSha256,
        string? LegalIssuanceFingerprint);

    private sealed record EndingCounts(int Receipts, int Audits, int OutboxMessages);

    private sealed record PropertyDispositionCounts(
        int Dispositions,
        int Receipts,
        int Audits,
        int OutboxMessages);

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int SourceAgreementId,
        int SuccessorAgreementId,
        int ReplacementAgreementId,
        int IssuedAgreementId,
        int ExecutedAgreementId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision);
}
