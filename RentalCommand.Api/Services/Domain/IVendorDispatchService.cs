using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Vendor SMS dispatch + performance scorecard. Frank assigns a work order to a vendor; the service
/// texts the vendor the job and asks them to reply DONE. The vendor's DONE reply (handled by
/// <see cref="ISmsInboundVendorDoneService"/>) closes the dispatch and the work order. Ratings accrue
/// into cached aggregates on the vendor and feed the scorecard.
/// </summary>
public interface IVendorDispatchService
{
    /// <summary>
    /// Assign <paramref name="vendorId"/> to the work order, create an open <c>VendorDispatch</c>, and
    /// enqueue the job SMS to the vendor's phone. Returns a result describing success or the specific
    /// failure (work order / vendor out of scope, or the vendor has no phone on file).
    /// </summary>
    Task<DispatchResult> DispatchAsync(int portfolioId, int workOrderId, DispatchWorkOrderRequest request, int? changedByUserId, CancellationToken ct = default);

    /// <summary>Record a 1–5 star rating and refresh the vendor's cached aggregates. Null when out of scope.</summary>
    Task<VendorRatingResponse?> RateAsync(int portfolioId, int vendorId, CreateVendorRatingRequest request, CancellationToken ct = default);

    /// <summary>Vendor performance scorecard, or null when the vendor is not in the caller's portfolio.</summary>
    Task<VendorScorecardResponse?> GetScorecardAsync(int portfolioId, int vendorId, CancellationToken ct = default);
}

/// <summary>Outcome of a dispatch attempt; <see cref="DispatchOutcome"/> drives the HTTP mapping.</summary>
public sealed record DispatchResult(DispatchOutcome Outcome, VendorDispatchResponse? Dispatch)
{
    public static DispatchResult Ok(VendorDispatchResponse dispatch) => new(DispatchOutcome.Dispatched, dispatch);
    public static DispatchResult NotFound() => new(DispatchOutcome.NotFound, null);
    public static DispatchResult NoPhone() => new(DispatchOutcome.VendorHasNoPhone, null);
}

public enum DispatchOutcome
{
    Dispatched,

    /// <summary>The work order or vendor is missing or out of the caller's portfolio.</summary>
    NotFound,

    /// <summary>The vendor exists but has no phone number, so no SMS can be sent.</summary>
    VendorHasNoPhone,
}
