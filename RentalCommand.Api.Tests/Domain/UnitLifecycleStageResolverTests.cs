using FluentAssertions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// The derived unit lifecycle stage (spec section 5) is the one piece of unit-page logic we unit-test:
/// it is a pure decision tree over an already-fetched aggregate, and the rail's correctness hinges on it.
/// Each case below maps to a representative branch of the spec's "first match wins" tree.
/// </summary>
public class UnitLifecycleStageResolverTests
{
    private static readonly DateTime Now = new(2026, 06, 20, 12, 0, 0, DateTimeKind.Utc);

    private static UnitStageInputs Inputs(
        UnitStatus unitStatus = UnitStatus.Vacant,
        LeaseSnapshot? currentLease = null,
        bool hasDraftOrPendingLease = false,
        bool hasOpenApplication = false,
        bool hasUpcomingShowing = false,
        bool hasUpcomingMoveInAppt = false,
        bool recentMoveOutSignal = false,
        decimal outstandingRentBalance = 0m)
        => new(
            unitStatus,
            currentLease,
            hasDraftOrPendingLease,
            hasOpenApplication,
            hasUpcomingShowing,
            hasUpcomingMoveInAppt,
            recentMoveOutSignal,
            outstandingRentBalance);

    private static LeaseSnapshot ActiveLease(DateTime? start, DateTime? end, LeaseStatus status = LeaseStatus.Active)
        => new(status, start, end);

    [Fact]
    public void OccupiedSteadyState_ResolvesToActive()
    {
        // Active lease, started well in the past, ends far out -> steady Active.
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddMonths(-6), Now.AddMonths(6)));

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Active);
    }

    [Fact]
    public void ActiveLease_EndingWithin90Days_ResolvesToRenewal()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddMonths(-11), Now.AddDays(30)));

        var (stage, label) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Renewal);
        label.Should().Contain("30"); // "Send renewal — lease ends in 30 days"
    }

    [Fact]
    public void ActiveLease_NoticeGiven_ResolvesToMoveOut()
    {
        // NoticeGiven wins over the renewal-window check even when the lease ends soon.
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddMonths(-11), Now.AddDays(20), LeaseStatus.NoticeGiven));

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.MoveOut);
    }

    [Fact]
    public void ActiveLease_FutureStartWithMoveInAppt_ResolvesToMoveIn()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Reserved,
            currentLease: ActiveLease(Now.AddDays(3), Now.AddMonths(12)),
            hasUpcomingMoveInAppt: true);

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.MoveIn);
    }

    [Fact]
    public void ActiveLease_StartedWithinLast14Days_ResolvesToMoveIn()
    {
        // Just-started lease (no appt) is still settling in -> Move-In.
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddDays(-5), Now.AddMonths(12)));

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.MoveIn);
    }

    [Fact]
    public void DraftLease_NoActive_ResolvesToLease()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Vacant,
            currentLease: null,
            hasDraftOrPendingLease: true);

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Lease);
    }

    [Fact]
    public void NoLease_OpenApplication_ResolvesToApplicant()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Vacant,
            currentLease: null,
            hasOpenApplication: true);

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Applicant);
    }

    [Fact]
    public void Vacant_RecentMoveOutSignal_ResolvesToTurnover()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Offline,
            currentLease: null,
            recentMoveOutSignal: true);

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Turnover);
    }

    [Fact]
    public void Vacant_UpcomingShowing_ResolvesToListed()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Vacant,
            currentLease: null,
            hasUpcomingShowing: true);

        var (stage, _) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Listed);
    }

    [Fact]
    public void Vacant_IdleNoSignals_ResolvesToReady()
    {
        var inputs = Inputs(unitStatus: UnitStatus.Vacant, currentLease: null);

        var (stage, label) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Ready);
        label.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Active_WithOutstandingBalance_NextBestActionPromptsCollection()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddMonths(-6), Now.AddMonths(6)),
            outstandingRentBalance: 1250m);

        var (stage, label) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Active);
        label.Should().Contain("1,250"); // "Collect $1,250.00" — formatted with the current culture's separators
    }

    [Fact]
    public void Active_NoBalance_NextBestActionSaysOnTrack()
    {
        var inputs = Inputs(
            unitStatus: UnitStatus.Occupied,
            currentLease: ActiveLease(Now.AddMonths(-6), Now.AddMonths(6)),
            outstandingRentBalance: 0m);

        var (stage, label) = UnitLifecycleStageResolver.Resolve(inputs, Now);

        stage.Should().Be(UnitLifecycleStage.Active);
        label.Should().Contain("track");
    }
}
