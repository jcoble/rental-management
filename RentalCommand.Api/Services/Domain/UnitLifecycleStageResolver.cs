using System.Globalization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The nine derived lifecycle stages a unit moves through (Ready -> ... -> Turnover -> Ready again).
/// There is no stored stage in v1 (spec section 5): the stage is computed per-unit on the dashboard from
/// already-fetched aggregate data. This enum is the seed for a future persisted column + portfolio board,
/// so the derivation is kept isolated in <see cref="UnitLifecycleStageResolver"/>.
/// </summary>
public enum UnitLifecycleStage
{
    Ready,
    Listed,
    Applicant,
    Lease,
    MoveIn,
    Active,
    Renewal,
    MoveOut,
    Turnover,
}

/// <summary>
/// Already-fetched inputs the stage derivation needs. Built by the dashboard service from the unit's
/// related rows (lease, applications, appointments, work orders, rent balance) — never inside the resolver,
/// which performs no I/O so it stays a pure, testable O(1) function.
/// </summary>
/// <param name="UnitStatus">The unit's occupancy status (Vacant/Occupied/Reserved/Offline).</param>
/// <param name="CurrentLease">In-force Active/NoticeGiven lease or pending signature lease; null when only historical leases remain.</param>
/// <param name="HasDraftOrPendingLease">True when a lease in {Draft, PendingSignature} exists for the unit (and no Active lease).</param>
/// <param name="HasOpenApplication">True when an open application {Submitted, UnderReview, Approved} exists that has not yet converted to a lease.</param>
/// <param name="HasUpcomingShowing">True when a Showing appointment is scheduled in the future.</param>
/// <param name="HasUpcomingMoveInAppt">True when a MoveIn appointment is scheduled in the future.</param>
/// <param name="RecentMoveOutSignal">True when there is a make-ready signal: last lease Expired/Terminated recently, a MoveOut inspection done, UnitStatus==Offline, or open work orders on the unit.</param>
/// <param name="OutstandingRentBalance">Outstanding (still-owed) rent balance for the unit's current lease, used only to shape the Active next-best-action.</param>
public sealed record UnitStageInputs(
    UnitStatus UnitStatus,
    LeaseSnapshot? CurrentLease,
    bool HasDraftOrPendingLease,
    bool HasOpenApplication,
    bool HasUpcomingShowing,
    bool HasUpcomingMoveInAppt,
    bool RecentMoveOutSignal,
    decimal OutstandingRentBalance);

/// <summary>Minimal lease projection the resolver reads (status, dates, and deposit-completion signal).</summary>
public sealed record LeaseSnapshot(
    LeaseStatus Status,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal SecurityDeposit = 0m,
    bool HasHeldSecurityDeposit = false);

/// <summary>
/// Derives a unit's lifecycle stage and its next-best-action label from an already-fetched aggregate,
/// implementing the spec's first-match-wins decision tree (section 5). Pure: no DB access, no clock read
/// (the caller passes <c>nowUtc</c>) — so it is fully unit-tested.
/// </summary>
public static class UnitLifecycleStageResolver
{
    /// <summary>Renewal window: an Active lease ending within this many days surfaces as Renewal.</summary>
    private const int RenewalWindowDays = 90;

    /// <summary>A lease that started within this many days is still in its Move-In settling period.</summary>
    private const int MoveInRecentDays = 14;

    public static (UnitLifecycleStage Stage, string NextBestActionLabel) Resolve(UnitStageInputs inputs, DateTime nowUtc)
    {
        var stage = ResolveStage(inputs, nowUtc);
        return (stage, NextBestAction(stage, inputs, nowUtc));
    }

    private static UnitLifecycleStage ResolveStage(UnitStageInputs inputs, DateTime nowUtc)
    {
        var lease = inputs.CurrentLease;

        if (lease is { Status: LeaseStatus.Active })
        {
            // An Active lease in notice has overriding priority over the renewal-window check.
            if (lease.Status == LeaseStatus.NoticeGiven)
            {
                return UnitLifecycleStage.MoveOut;
            }

            // Move-In: lease starts in the future, OR a move-in appointment is upcoming, OR the lease only
            // just started (still settling in). Checked before Renewal so a brand-new lease never reads as Renewal.
            if (IsFuture(lease.StartDate, nowUtc)
                || inputs.HasUpcomingMoveInAppt
                || (StartedWithinDays(lease.StartDate, nowUtc, MoveInRecentDays) && NeedsDepositCompletion(lease)))
            {
                return UnitLifecycleStage.MoveIn;
            }

            if (EndsWithinDays(lease.EndDate, nowUtc, RenewalWindowDays))
            {
                return UnitLifecycleStage.Renewal;
            }

            return UnitLifecycleStage.Active;
        }

        // A NoticeGiven lease that is no longer the "Active" snapshot can still drive Move-Out.
        if (lease is { Status: LeaseStatus.NoticeGiven })
        {
            return UnitLifecycleStage.MoveOut;
        }

        // Not currently leased.
        if (inputs.HasDraftOrPendingLease)
        {
            return UnitLifecycleStage.Lease;
        }

        if (inputs.HasOpenApplication)
        {
            return UnitLifecycleStage.Applicant;
        }

        if (inputs.RecentMoveOutSignal)
        {
            return UnitLifecycleStage.Turnover;
        }

        if (inputs.HasUpcomingShowing)
        {
            return UnitLifecycleStage.Listed;
        }

        return UnitLifecycleStage.Ready;
    }

    private static string NextBestAction(UnitLifecycleStage stage, UnitStageInputs inputs, DateTime nowUtc)
        => stage switch
        {
            UnitLifecycleStage.Ready => "List this unit",
            UnitLifecycleStage.Listed => "Review applicants / schedule showing",
            UnitLifecycleStage.Applicant => "Screen & decide on the applicant",
            UnitLifecycleStage.Lease => "Send lease for signature / mark signed",
            UnitLifecycleStage.MoveIn => "Confirm move-in / collect deposit",
            UnitLifecycleStage.Active => inputs.OutstandingRentBalance > 0m
                ? $"Collect {FormatMoney(inputs.OutstandingRentBalance)}"
                : "Rent on track",
            UnitLifecycleStage.Renewal => $"Send renewal — lease ends in {DaysUntilEnd(inputs.CurrentLease, nowUtc)} days",
            UnitLifecycleStage.MoveOut => "Schedule move-out inspection",
            UnitLifecycleStage.Turnover => "Track make-ready / mark rent-ready",
            _ => "Open unit",
        };

    private static bool IsFuture(DateTime? date, DateTime nowUtc) => date.HasValue && date.Value > nowUtc;

    private static bool StartedWithinDays(DateTime? start, DateTime nowUtc, int days)
        => start.HasValue && start.Value <= nowUtc && start.Value >= nowUtc.AddDays(-days);

    private static bool NeedsDepositCompletion(LeaseSnapshot lease)
        => lease.SecurityDeposit > 0m && !lease.HasHeldSecurityDeposit;

    private static bool EndsWithinDays(DateTime? end, DateTime nowUtc, int days)
        => end.HasValue && end.Value >= nowUtc && end.Value <= nowUtc.AddDays(days);

    private static int DaysUntilEnd(LeaseSnapshot? lease, DateTime nowUtc)
    {
        if (lease?.EndDate is not { } end)
        {
            return 0;
        }

        var days = (int)Math.Ceiling((end - nowUtc).TotalDays);
        return days < 0 ? 0 : days;
    }

    /// <summary>Formats a money amount as <c>$1,234.56</c> with grouping, independent of host culture.</summary>
    private static string FormatMoney(decimal amount)
        => "$" + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
}
