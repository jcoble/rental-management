using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Vendors;

/// <summary>Admits one explicit, retry-safe request to text a vendor for a W-9.</summary>
public sealed record RequestVendorW9Command(
    int PortfolioId,
    int VendorId,
    string ClientOperationId,
    int? ChangedByUserId,
    DateTime RequestedAtUtc) : IAtomicCommandData;

public enum RequestVendorW9Outcome
{
    Queued,
    NotFound,
    VendorHasNoPhone,
}

/// <summary>Canonical receipt result for a W-9 request admission.</summary>
public sealed record RequestVendorW9Result(
    RequestVendorW9Outcome Outcome,
    string? Phone) : IAtomicResultData;
