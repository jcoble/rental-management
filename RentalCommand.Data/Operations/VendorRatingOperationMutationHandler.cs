using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class CreateVendorRatingHandler
    : IAtomicCommandHandler<CreateVendorRatingCommand, VendorRatingMutationResult>,
      IAtomicReplayAuthorizer<CreateVendorRatingCommand>
{
    public async Task<VendorRatingMutationResult> HandleAsync(
        CreateVendorRatingCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        VendorRatingOperationValidation.Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Vendor, command.VendorId, ct);

        var vendor = await AuthorizedVendors(command, attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(ct);
        if (vendor is null)
            return new(OperationMutationOutcome.NotFound, 0, command.VendorId);

        var rating = new VendorRating
        {
            PortfolioId = command.PortfolioId,
            VendorId = command.VendorId,
            WorkOrderId = command.WorkOrderId,
            Stars = command.Stars,
            Comment = Normalize(command.Comment),
            CreatedAtUtc = now,
        };
        attempt.Persistence.Add(rating);
        attempt.UseDatabaseWallClockForAudit(now);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(VendorRating),
            rating.Id,
            AuditLogOperation.Created,
            command.Actor.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                rating.VendorId,
                rating.WorkOrderId,
                rating.Stars,
                rating.Comment,
            }),
            ChangeReason: "Rated vendor."), now);

        var aggregate = await attempt.Persistence.Query<VendorRating>()
            .AsNoTracking()
            .Where(item => item.PortfolioId == command.PortfolioId && item.VendorId == command.VendorId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Average = group.Average(item => (decimal)item.Stars),
            })
            .SingleAsync(ct);
        vendor.RatingCount = aggregate.Count;
        vendor.AverageRating = Math.Round(aggregate.Average, 2);
        vendor.UpdatedAt = now;
        attempt.BindSemanticAudit(vendor, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Vendor),
            vendor.Id,
            AuditLogOperation.Updated,
            command.Actor.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                vendor.AverageRating,
                vendor.RatingCount,
            }),
            ChangeReason: "Refreshed vendor rating aggregates."));
        await attempt.FlushBusinessAsync(ct);

        var snapshot = await VendorRatingSnapshot.LoadAsync(
            attempt.Persistence, command.PortfolioId, rating.Id, ct);
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            command.PortfolioId,
            nameof(VendorRating),
            rating.Id,
            $"vendor-rating-create:{command.DeliveryIdempotencyKey}",
            now));
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            command.PortfolioId,
            nameof(Vendor),
            vendor.Id,
            $"vendor-rating-vendor-update:{command.DeliveryIdempotencyKey}",
            now));
        return new(OperationMutationOutcome.Applied, rating.Id, vendor.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateVendorRatingCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        VendorRatingOperationValidation.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedVendors(command, persistence, now, tracking: false).AnyAsync(ct))
            throw new UnauthorizedAccessException("The active assignment cannot rate this vendor.");
    }

    private static IQueryable<Vendor> AuthorizedVendors(
        CreateVendorRatingCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        bool tracking)
    {
        var assignments = StaffOperationAuthorization.ActiveAssignments(
            command.PortfolioId, command.Actor, CapabilityKeys.WorkManage, persistence, now);
        var workOrders = StaffOperationAuthorization.AuthorizedWorkOrders(
            command.PortfolioId, command.Actor, CapabilityKeys.WorkManage, persistence, now, tracking);
        var query = persistence.Query<Vendor>().Where(vendor =>
            vendor.Id == command.VendorId &&
            vendor.PortfolioId == command.PortfolioId &&
            (command.WorkOrderId.HasValue
                ? workOrders.Any(workOrder =>
                    workOrder.Id == command.WorkOrderId.Value && workOrder.VendorId == vendor.Id)
                : assignments.Any()));
        return tracking ? query : query.AsNoTracking();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class VendorRatingOperationValidation
{
    internal static void Validate(CreateVendorRatingCommand command)
    {
        if (command.PortfolioId <= 0 || command.VendorId <= 0 || command.Actor.UserId <= 0 ||
            command.Actor.AuthSessionId == Guid.Empty || command.Actor.AccessContextId <= 0 ||
            command.Actor.AccessRevision <= 0 || command.Stars is < 1 or > 5 ||
            string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A valid vendor rating is required.");
        if (command.WorkOrderId is <= 0)
            throw new DomainValidationException("The work order identity is invalid.");
        if (command.Comment?.Length > 2000)
            throw new DomainValidationException("The rating comment cannot exceed 2,000 characters.");
    }
}

internal static class VendorRatingSnapshot
{
    internal static async Task<string> LoadAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int ratingId, CancellationToken ct)
    {
        var row = await persistence.Query<VendorRating>()
            .AsNoTracking()
            .Where(item => item.Id == ratingId && item.PortfolioId == portfolioId)
            .Select(item => new
            {
                item.Id,
                item.VendorId,
                item.WorkOrderId,
                item.Stars,
                item.Comment,
                item.CreatedAtUtc,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(row);
    }
}
