using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Leasing;

namespace RentalCommand.IntegrationTests;

public sealed class LeasingWriteDescriptorTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly RentalCommandDbContext _db = new(
        new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    [Fact]
    public void AllTwentyOperationsIncludingPossession_PreserveLegacyOperationsContractsAndLockPrefixes()
    {
        Describe<PrepareMoveInCommand, PrepareMoveInResult>(Prepare()).Should().BeEquivalentTo(
            new Descriptor("lease-management.prepare-move-in", "lease-management.prepare-move-in.v2",
                WriteLockProtocol.PrepareMoveIn, ["Unit"], ["RentalApplication"]));
        Describe<RecordLeaseEndingDispositionCommand, RecordLeaseEndingDispositionResult>(Ending()).Should()
            .BeEquivalentTo(new Descriptor("lease-management.ending-disposition",
                "lease-management.ending-disposition.v1", null, [], []));
        Describe<CancelPlannedRelationshipCommand, CancelPlannedRelationshipResult>(Cancel()).Should()
            .BeEquivalentTo(new Descriptor("lease-management.cancel-planned",
                "lease-management.cancel-planned.v1", WriteLockProtocol.LeaseManagement,
                ["LeaseManagement"], []));
        Describe<TransferLeaseManagementCommand, TransferLeaseManagementResult>(Transfer()).Should()
            .BeEquivalentTo(new Descriptor("lease-management.transfer-unit",
                "lease-management.transfer-unit.v1", WriteLockProtocol.LeaseTransfer,
                ["Unit", "Unit", "LeaseManagement"], []));
        Describe<CancelLeaseAgreementSuccessorDraftCommand,
            CancelLeaseAgreementSuccessorDraftResult>(CancelSuccessor()).Should().BeEquivalentTo(
            new Descriptor("lease-agreement.successor-draft.cancel",
                "lease-agreement.successor-draft.cancel.v1", WriteLockProtocol.LeaseAgreementDraft,
                ["AuthSession", "WorkspaceAccessContext", "LeaseManagement"], []));
        Describe<CreateLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>(CreateAddendum()).Should()
            .BeEquivalentTo(new Descriptor("lease-addendum.draft.create", "lease-addendum.draft.mutation.v1",
                WriteLockProtocol.LeaseManagement, ["LeaseManagement"], []));
        Describe<EditLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>(EditAddendum()).Should()
            .BeEquivalentTo(new Descriptor("lease-addendum.draft.edit", "lease-addendum.draft.mutation.v1",
                WriteLockProtocol.LeaseManagement, ["LeaseManagement"], []));
        Describe<CorrectLeaseAddendumDraftCommand, LeaseAddendumDraftMutationResult>(CorrectAddendum()).Should()
            .BeEquivalentTo(new Descriptor("lease-addendum.draft.correct", "lease-addendum.draft.mutation.v1",
                WriteLockProtocol.LeaseManagement, ["LeaseManagement"], []));
        Describe<EditLeaseAgreementDraftCommand, LeaseAgreementDraftMutationResult>(EditAgreement()).Should()
            .BeEquivalentTo(Agreement("lease-agreement.draft.edit", "lease-agreement.draft.edit.v1"));
        Describe<CreateLeaseAgreementSuccessorDraftCommand,
            LeaseAgreementDraftMutationResult>(CreateSuccessor()).Should().BeEquivalentTo(
            Agreement("lease-agreement.successor-draft.create", "lease-agreement.successor-draft.create.v2"));
        Describe<ReplaceIssuedAgreementWithDraftCommand,
            LeaseAgreementDraftMutationResult>(ReplaceIssued()).Should().BeEquivalentTo(
            Agreement("lease-agreement.issued-replacement-draft.create",
                "lease-agreement.successor-draft.create.v2"));
        Describe<CreatePropertyDispositionCommand, CreatePropertyDispositionResult>(Disposition()).Should()
            .BeEquivalentTo(new Descriptor("property-disposition.create", "property-disposition.create.v1",
                WriteLockProtocol.PropertyDisposition, ["Property"], []));
        Describe<GivePossessionCommand, GivePossessionResult>(Give()).Should().BeEquivalentTo(
            new Descriptor("lease-management.give-possession", "lease-management.give-possession.v1",
                WriteLockProtocol.Possession, ["Unit", "LeaseManagement"], []));
        Describe<ReconcileHistoricalPossessionCommand,
            ReconcileHistoricalPossessionResult>(Reconcile()).Should().BeEquivalentTo(
            new Descriptor("lease-management.reconcile-historical-possession",
                "lease-management.reconcile-historical-possession.v1", WriteLockProtocol.Possession,
                ["Unit", "LeaseManagement"], []));
        Describe<ConfirmMoveInCommand, ConfirmMoveInResult>(Confirm()).Should().BeEquivalentTo(
            new Descriptor("lease-management.confirm-move-in", "lease-management.confirm-move-in.v1",
                null, [], []));
        Describe<ReturnPossessionCommand, ReturnPossessionResult>(Return()).Should().BeEquivalentTo(
            new Descriptor("lease-management.return-possession", "lease-management.return-possession.v1",
                WriteLockProtocol.Possession, ["Unit", "LeaseManagement"], []));
        Describe<CompleteTurnoverCommand, CompleteTurnoverResult>(Turnover()).Should().BeEquivalentTo(
            new Descriptor("unit.complete-turnover", "unit.complete-turnover.v1", null, [], []));
        Describe<VoidLeaseAgreementCommand, VoidLegalArtifactResult>(VoidAgreement()).Should().BeEquivalentTo(
            new Descriptor("lease-agreement.void", "lease-agreement.void.v1",
                WriteLockProtocol.LeaseManagement, ["LeaseManagement"], []));
        Describe<VoidLeaseAddendumCommand, VoidLegalArtifactResult>(VoidAddendum()).Should().BeEquivalentTo(
            new Descriptor("lease-addendum.void", "lease-addendum.void.v1",
                WriteLockProtocol.LeaseManagement, ["LeaseManagement"], []));
        Describe<CloseTenantAccountCommand, CloseTenantAccountResult>(CloseAccount()).Should().BeEquivalentTo(
            new Descriptor("tenant-account.close", "tenant-account.close.v1", null, [], []));
    }

    [Fact]
    public void PossessionFamilyFingerprints_StillExcludeExactlyTheLegacyReplayAuthorizationAndClockFields()
    {
        var common = new[] { "AuthSessionId", "AccessContextId", "ExpectedAccessRevision",
            "DeliveryIdempotencyKey" };
        var commonTypes = new[]
        {
            typeof(PrepareMoveInCommand), typeof(RecordLeaseEndingDispositionCommand),
            typeof(CancelLeaseAgreementSuccessorDraftCommand), typeof(CreateLeaseAddendumDraftCommand),
            typeof(EditLeaseAddendumDraftCommand), typeof(CorrectLeaseAddendumDraftCommand),
            typeof(EditLeaseAgreementDraftCommand), typeof(CreateLeaseAgreementSuccessorDraftCommand),
            typeof(ReplaceIssuedAgreementWithDraftCommand), typeof(CreatePropertyDispositionCommand),
        };
        foreach (var type in commonTypes)
        {
            Ignored(type).Should().Equal(common);
        }

        Ignored(typeof(TransferLeaseManagementCommand)).Should().Equal(
            "AuthSessionId", "AccessContextId", "ExpectedAccessRevision", "BusinessNowUtc",
            "DeliveryIdempotencyKey");
        Ignored(typeof(CancelPlannedRelationshipCommand)).Should().Equal(
            "AuthSessionId", "AccessContextId", "ExpectedAccessRevision", "BusinessNowUtc",
            "DeliveryIdempotencyKey");
        var possessionTypes = new[]
        {
            typeof(GivePossessionCommand), typeof(ReconcileHistoricalPossessionCommand),
            typeof(ConfirmMoveInCommand), typeof(ReturnPossessionCommand),
            typeof(CompleteTurnoverCommand),
        };
        foreach (var type in possessionTypes)
        {
            Ignored(type).Should().Equal("AuthSessionId", "AccessContextId", "ExpectedAccessRevision",
                "BusinessNowUtc", "DeliveryIdempotencyKey");
        }
        Ignored(typeof(VoidLeaseAgreementCommand)).Should().Equal(common);
        Ignored(typeof(VoidLeaseAddendumCommand)).Should().Equal(common);
        Ignored(typeof(CloseTenantAccountCommand)).Should().Equal(common);
    }

    [Fact]
    public void PossessionFamilyFrozenStoredResults_DecodeWithEveryDistinctLegacyContractAndJsonShape()
    {
        AssertFrozen<PrepareMoveInResult>("lease-management.prepare-move-in.v2", """
            {"Outcome":0,"ApplicationId":2,"LeaseManagementId":3,"TenantAccountId":4,"LeaseAgreementId":5,"OpeningBalanceLedgerEntryId":6,"SecurityDepositAccountId":7,"TenantIds":[8],"LeaseManagementPartyIds":[9],"LeaseAgreementSignerIds":[10],"Error":null}
            """,
            "Outcome", "ApplicationId", "LeaseManagementId", "TenantAccountId", "LeaseAgreementId",
            "OpeningBalanceLedgerEntryId", "SecurityDepositAccountId", "TenantIds",
            "LeaseManagementPartyIds", "LeaseAgreementSignerIds", "Error");
        AssertFrozen<RecordLeaseEndingDispositionResult>("lease-management.ending-disposition.v1", """
            {"Outcome":0,"LeaseManagementId":2,"EndingDisposition":1,"EndingDispositionDecidedAtUtc":null,"EndingDispositionDecidedByUserId":4,"NoticeGivenAtUtc":null,"PlannedMoveOutAtUtc":null,"Error":null}
            """,
            "Outcome", "LeaseManagementId", "EndingDisposition", "EndingDispositionDecidedAtUtc",
            "EndingDispositionDecidedByUserId", "NoticeGivenAtUtc", "PlannedMoveOutAtUtc", "Error");
        AssertFrozen<CancelPlannedRelationshipResult>("lease-management.cancel-planned.v1", """
            {"Outcome":0,"LeaseManagementId":2,"UnitId":3,"CanceledAtUtc":null,"AccountClosedAtUtc":null,"CanceledAgreementDraftIds":[4],"CanceledAddendumDraftIds":[5],"RevokedAccessIds":[6],"RetainedAccessIds":[7],"Error":null}
            """,
            "Outcome", "LeaseManagementId", "UnitId", "CanceledAtUtc", "AccountClosedAtUtc",
            "CanceledAgreementDraftIds", "CanceledAddendumDraftIds", "RevokedAccessIds",
            "RetainedAccessIds", "Error");
        AssertFrozen<TransferLeaseManagementResult>("lease-management.transfer-unit.v1", """
            {"Outcome":0,"TransferPublicId":"11111111-1111-1111-1111-111111111111","SourceLeaseManagementId":2,"SourceUnitId":3,"DestinationLeaseManagementId":4,"DestinationUnitId":5,"DestinationTenantAccountId":6,"DestinationAgreementId":7,"DestinationSecurityDepositAccountId":8,"TurnoverPeriodId":9,"SourcePossessionReturnedAtUtc":null,"DestinationPossessionGivenAtUtc":null,"CarriedTenantBalance":10,"CarriedSecurityDeposit":11,"EndedSourcePartyIds":[12],"DestinationPartyIds":[13],"DestinationSignerIds":[14],"RevokedSourceAccessIds":[15],"DestinationAccessIds":[16],"TenantLedgerEntryIds":[17],"SecurityDepositEntryIds":[18],"Error":null}
            """,
            "Outcome", "TransferPublicId", "SourceLeaseManagementId", "SourceUnitId",
            "DestinationLeaseManagementId", "DestinationUnitId", "DestinationTenantAccountId",
            "DestinationAgreementId", "DestinationSecurityDepositAccountId", "TurnoverPeriodId",
            "SourcePossessionReturnedAtUtc", "DestinationPossessionGivenAtUtc", "CarriedTenantBalance",
            "CarriedSecurityDeposit", "EndedSourcePartyIds", "DestinationPartyIds",
            "DestinationSignerIds", "RevokedSourceAccessIds", "DestinationAccessIds",
            "TenantLedgerEntryIds", "SecurityDepositEntryIds", "Error");
        AssertFrozen<CancelLeaseAgreementSuccessorDraftResult>(
            "lease-agreement.successor-draft.cancel.v1", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAgreementId":3,"DraftCanceledAtUtc":null,"DraftCanceledByUserId":4,"DraftCancellationReason":"reason","Error":null}
            """,
            "Outcome", "LeaseManagementId", "LeaseAgreementId", "DraftCanceledAtUtc",
            "DraftCanceledByUserId", "DraftCancellationReason", "Error");
        AssertFrozen<LeaseAddendumDraftMutationResult>("lease-addendum.draft.mutation.v1", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAddendumId":3,"SeriesPublicId":"11111111-1111-1111-1111-111111111111","VersionNumber":4,"DraftRevision":5,"SourceAddendumId":6,"LeaseAddendumSignerIds":[7],"FinancialEffectIds":[8],"Error":null}
            """,
            "Outcome", "LeaseManagementId", "LeaseAddendumId", "SeriesPublicId", "VersionNumber",
            "DraftRevision", "SourceAddendumId", "LeaseAddendumSignerIds", "FinancialEffectIds", "Error");
        AssertFrozen<LeaseAgreementDraftMutationResult>("lease-agreement.draft.edit.v1", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAgreementId":3,"VersionNumber":4,"DraftRevision":5,"SourceAgreementId":6,"LeaseAgreementSignerIds":[7],"AddendumDecisionIds":[8],"ReplacementAddendumIds":[9],"Error":null}
            """,
            "Outcome", "LeaseManagementId", "LeaseAgreementId", "VersionNumber", "DraftRevision",
            "SourceAgreementId", "LeaseAgreementSignerIds", "AddendumDecisionIds",
            "ReplacementAddendumIds", "Error");
        AssertFrozen<LeaseAgreementDraftMutationResult>("lease-agreement.successor-draft.create.v2", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAgreementId":3,"VersionNumber":4,"DraftRevision":5,"SourceAgreementId":6,"LeaseAgreementSignerIds":[7],"AddendumDecisionIds":[8],"ReplacementAddendumIds":[9],"Error":null}
            """,
            "Outcome", "LeaseManagementId", "LeaseAgreementId", "VersionNumber", "DraftRevision",
            "SourceAgreementId", "LeaseAgreementSignerIds", "AddendumDecisionIds",
            "ReplacementAddendumIds", "Error");
        AssertFrozen<CreatePropertyDispositionResult>("property-disposition.create.v1", """
            {"Outcome":0,"DispositionId":2,"LeaseManagementCount":3,"TenantAccountCount":4}
            """,
            "Outcome", "DispositionId", "LeaseManagementCount", "TenantAccountCount");
        AssertFrozen<GivePossessionResult>("lease-management.give-possession.v1", """
            {"Outcome":0,"LeaseManagementId":2,"UnitId":3,"PossessionGivenAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "UnitId", "PossessionGivenAtUtc", "Error");
        AssertFrozen<ReconcileHistoricalPossessionResult>(
            "lease-management.reconcile-historical-possession.v1", """
            {"Outcome":0,"LeaseManagementId":2,"UnitId":3,"PossessionGivenAtUtc":"2026-08-22T00:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "UnitId", "PossessionGivenAtUtc", "Error");
        AssertFrozen<ConfirmMoveInResult>("lease-management.confirm-move-in.v1", """
            {"Outcome":0,"LeaseManagementId":2,"UnitId":3,"PossessionGivenAtUtc":"2026-08-22T12:00:00Z","SecurityDepositEntryId":4,"TenantLedgerEntryId":5,"CompletedAppointmentId":6,"Error":null}
            """, "Outcome", "LeaseManagementId", "UnitId", "PossessionGivenAtUtc",
            "SecurityDepositEntryId", "TenantLedgerEntryId", "CompletedAppointmentId", "Error");
        AssertFrozen<ReturnPossessionResult>("lease-management.return-possession.v1", """
            {"Outcome":0,"LeaseManagementId":2,"UnitId":3,"TurnoverPeriodId":4,"PossessionReturnedAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "UnitId", "TurnoverPeriodId",
            "PossessionReturnedAtUtc", "Error");
        AssertFrozen<CompleteTurnoverResult>("unit.complete-turnover.v1", """
            {"Outcome":0,"UnitId":3,"TurnoverPeriodId":4,"CompletedAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "UnitId", "TurnoverPeriodId", "CompletedAtUtc", "Error");
        AssertFrozen<VoidLegalArtifactResult>("lease-agreement.void.v1", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAgreementId":3,"LeaseAddendumId":null,"VoidedAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "LeaseAgreementId", "LeaseAddendumId",
            "VoidedAtUtc", "Error");
        AssertFrozen<VoidLegalArtifactResult>("lease-addendum.void.v1", """
            {"Outcome":0,"LeaseManagementId":2,"LeaseAgreementId":null,"LeaseAddendumId":3,"VoidedAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "LeaseAgreementId", "LeaseAddendumId",
            "VoidedAtUtc", "Error");
        AssertFrozen<CloseTenantAccountResult>("tenant-account.close.v1", """
            {"Outcome":0,"LeaseManagementId":2,"TenantAccountId":3,"ClosedAtUtc":"2026-08-22T12:00:00Z","Error":null}
            """, "Outcome", "LeaseManagementId", "TenantAccountId", "ClosedAtUtc", "Error");
    }

    private Descriptor Describe<TCommand, TResult>(TCommand command)
        where TCommand : notnull, IAtomicCommandData where TResult : notnull
    {
        var write = LeasingWriteSupport.Write<TCommand, TResult>(_db, command);
        return new(write.OperationName, write.ResultContract, write.LockPlan.Protocol,
            write.LockPlan.Locks.Select(item => item.LockNamespace).ToArray(),
            write.LockPlan.DeferredLockNamespaces.ToArray());
    }

    private static void AssertFrozen<TResult>(string contract, string storedJson, params string[] properties)
        where TResult : notnull
    {
        var codec = new AtomicJsonResultCodec<TResult>(contract);
        var decoded = codec.Deserialize(storedJson);
        using var stored = JsonDocument.Parse(storedJson);
        using var replayed = JsonDocument.Parse(codec.Serialize(decoded));

        stored.RootElement.EnumerateObject().Select(item => item.Name).Should().Equal(properties);
        JsonElement.DeepEquals(stored.RootElement, replayed.RootElement).Should().BeTrue();
    }

    private static Descriptor Agreement(string operation, string contract) => new(
        operation, contract, WriteLockProtocol.LeaseAgreementDraft,
        ["AuthSession", "WorkspaceAccessContext", "LeaseManagement"], []);

    private static string[] Ignored(Type type) => type.GetProperties()
        .Where(property => property.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null
            || type.GetInterfaces().SelectMany(contract => contract.GetProperties()).Any(contractProperty =>
                contractProperty.Name == property.Name
                && contractProperty.PropertyType == property.PropertyType
                && contractProperty.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null))
        .Select(property => property.Name).ToArray();

    private static PrepareMoveInCommand Prepare() => new(1, 2, 3, 4, SessionId, 5, 6, null,
        new DateOnly(2026, 9, 1), [], null, LeaseAgreementTermType.FixedTerm,
        new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), 1000m, 1, 500m, 25m, 5,
        1, "{}", false, null, null, null, "delivery");

    private static RecordLeaseEndingDispositionCommand Ending() => new(1, 2, 3,
        LeaseManagementEndingDisposition.NonRenewalMoveOut, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
        "reason", 4, SessionId, 5, 6, "delivery");

    private static CancelPlannedRelationshipCommand Cancel() => new(1, 2, 3, 4, SessionId, 5, 6,
        DateTime.UtcNow, "reason", null, "draft reason", [], "delivery");

    private static TransferLeaseManagementCommand Transfer() => new(1, 2, 30, 20, 4, SessionId, 5, 6,
        DateTime.UtcNow, Guid.NewGuid(), new DateOnly(2026, 9, 1), null, false, null, 7, true, true,
        "reason", "delivery");

    private static CancelLeaseAgreementSuccessorDraftCommand CancelSuccessor() =>
        new(1, 2, 3, "reason", 4, SessionId, 5, 6, "delivery");

    private static CreateLeaseAddendumDraftCommand CreateAddendum() => new(1, 2, 3, "A-1",
        LeaseAddendumPurpose.Other, new DateOnly(2026, 9, 1), null, 1, "{}", 4, [], [],
        5, SessionId, 6, 7, "delivery");

    private static EditLeaseAddendumDraftCommand EditAddendum() => new(1, 2, 3, 1, "A-1",
        LeaseAddendumPurpose.Other, new DateOnly(2026, 9, 1), null, 1, "{}", 4, [], [],
        5, SessionId, 6, 7, "delivery");

    private static CorrectLeaseAddendumDraftCommand CorrectAddendum() => new(1, 2, 3,
        new DateOnly(2026, 9, 1), 4, SessionId, 5, 6, "delivery");

    private static EditLeaseAgreementDraftCommand EditAgreement() => new(1, 2, 3, 1, "L-1",
        LeaseAgreementTermType.FixedTerm, new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31),
        new DateOnly(2026, 9, 1), 1000m, 1, 500m, 25m, 5, 1, "{}", null, [],
        4, SessionId, 5, 6, "delivery");

    private static CreateLeaseAgreementSuccessorDraftCommand CreateSuccessor() => new(1, 2, 3,
        LeaseAgreementChangeType.Renewal, new DateOnly(2027, 9, 1), new DateOnly(2028, 8, 31),
        new DateOnly(2027, 9, 1), null, null, [], 4, SessionId, 5, 6, "delivery");

    private static ReplaceIssuedAgreementWithDraftCommand ReplaceIssued() =>
        new(1, 2, 3, null, "reason", 4, SessionId, 5, 6, "delivery");

    private static CreatePropertyDispositionCommand Disposition() => new(1, 2, DateTime.UtcNow,
        100000m, 5000m, "buyer", "memo", 4, SessionId, 5, 6, "delivery");

    private static GivePossessionCommand Give() => new(
        1, 2, 3, 4, SessionId, 5, 6, DateTime.UtcNow, "delivery");

    private static ReconcileHistoricalPossessionCommand Reconcile() => new(
        1, 2, 3, new DateOnly(2026, 8, 22), 4, SessionId, 5, 6, DateTime.UtcNow, "delivery");

    private static ConfirmMoveInCommand Confirm() => new(
        1, 2, 3, null, null, null, null, 4, SessionId, 5, 6, DateTime.UtcNow, "delivery");

    private static ReturnPossessionCommand Return() => new(
        1, 2, 3, 4, SessionId, 5, 6, DateTime.UtcNow, [], [], "reason", "delivery");

    private static CompleteTurnoverCommand Turnover() => new(
        1, 3, 7, 4, SessionId, 5, 6, DateTime.UtcNow, "delivery");

    private static VoidLeaseAgreementCommand VoidAgreement() => new(
        1, 2, 3, "reason", null, 4, SessionId, 5, 6, "delivery");

    private static VoidLeaseAddendumCommand VoidAddendum() => new(
        1, 2, 3, "reason", null, 4, SessionId, 5, 6, "delivery");

    private static CloseTenantAccountCommand CloseAccount() => new(
        1, 2, 3, "reason", null, 4, SessionId, 5, 6, "delivery");

    private sealed record Descriptor(string Operation, string Contract, WriteLockProtocol? Protocol,
        string[] Locks, string[] DeferredLocks);
}
