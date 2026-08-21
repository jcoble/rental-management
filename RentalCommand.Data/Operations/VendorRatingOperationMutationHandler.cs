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
    : IAtomicCommandHandler<CreateVendorRatingCommand, VendorRatingMutationResult>
{
    public const string ResultContract = "vendor-rating.create.v1";

    private readonly RentalCommandDbContext _db;

    public CreateVendorRatingHandler(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<CreateVendorRatingCommand, VendorRatingMutationResult> Write(
        CreateVendorRatingCommand command,
        RentalCommandDbContext db)
    {
        var handler = new CreateVendorRatingHandler(db);
        var locks = command.WorkOrderId is int workOrderId
            ? new WriteLockPlan(WriteLockProtocol.WorkOrderVendor,
                WriteLock.For("WorkOrder", workOrderId), WriteLock.For("Vendor", command.VendorId))
            : new WriteLockPlan(WriteLockProtocol.Vendor,
                WriteLock.For("Vendor", command.VendorId));
        return new TransactionalWrite<CreateVendorRatingCommand, VendorRatingMutationResult>(
            "vendor-rating.create", WriteIdempotencyPolicy.Required, command, ResultContract,
            locks, handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public Task<VendorRatingMutationResult> HandleAsync(
        CreateVendorRatingCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RetiredPath();

    public async Task<VendorRatingMutationResult> ExecuteAsync(
        CreateVendorRatingCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        VendorRatingOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;

        var vendor = await AuthorizedVendors(
                command, _db, businessNow, securityNow, tracking: true)
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
            CreatedAtUtc = businessNow,
        };
        _db.Add(rating);
        context.UseDatabaseWallClockForAudit(businessNow);
        await context.FlushBusinessAsync(ct);

        context.StageSemanticEvent(new AtomicSemanticAudit(
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
            ChangeReason: "Rated vendor."), businessNow);

        var aggregate = await _db.Set<VendorRating>()
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
        vendor.UpdatedAt = businessNow;
        context.BindSemanticAudit(vendor, new AtomicSemanticAudit(
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
        await context.FlushBusinessAsync(ct);

        var snapshot = await VendorRatingSnapshot.LoadAsync(
            _db, command.PortfolioId, rating.Id, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            command.PortfolioId,
            nameof(VendorRating),
            rating.Id,
            $"vendor-rating-create:{command.DeliveryIdempotencyKey}",
            businessNow));
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            command.PortfolioId,
            nameof(Vendor),
            vendor.Id,
            $"vendor-rating-vendor-update:{command.DeliveryIdempotencyKey}",
            businessNow));
        return new(OperationMutationOutcome.Applied, rating.Id, vendor.Id, snapshot);
    }

    public Task AuthorizeReplayAsync(
        CreateVendorRatingCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RetiredPath();

    public async Task AuthorizeAsync(
        CreateVendorRatingCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        VendorRatingOperationValidation.Validate(command);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedVendors(
                command, _db, command.BusinessNowUtc, securityNow, tracking: false).AnyAsync(ct))
            throw new UnauthorizedAccessException("The active assignment cannot rate this vendor.");
    }

    private static InvalidOperationException RetiredPath() => new(
        "Vendor ratings must use the shared write executor.");

    private static IQueryable<Vendor> AuthorizedVendors(
        CreateVendorRatingCommand command,
        RentalCommandDbContext db,
        DateTime businessNow,
        DateTime securityNow,
        bool tracking)
    {
        var assignments = StaffOperationAuthorization.ActiveAssignments(
            command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
            db, businessNow, securityNow);
        var workOrders = StaffOperationAuthorization.AuthorizedWorkOrders(
            command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
            db, businessNow, securityNow, tracking);
        var query = db.Set<Vendor>().Where(vendor =>
            vendor.Id == command.VendorId &&
            vendor.PortfolioId == command.PortfolioId &&
            (command.WorkOrderId.HasValue
                ? workOrders.Any(workOrder =>
                    workOrder.Id == command.WorkOrderId.Value && workOrder.VendorId == vendor.Id &&
                    workOrder.Status != WorkOrderStatus.Cancelled &&
                    workOrder.Status != WorkOrderStatus.Archived)
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
        RentalCommandDbContext db, int portfolioId, int ratingId, CancellationToken ct)
    {
        var row = await db.Set<VendorRating>()
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
