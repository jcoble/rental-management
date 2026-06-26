using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public static class PaymentAttentionQueryExtensions
{
    public static IQueryable<Payment> ForCurrentLeaseAttention(this IQueryable<Payment> query, DateTime asOf)
    {
        var asOfDate = asOf.Date;

        return query.Where(p =>
            p.Lease != null &&
            ((p.Lease.Status == LeaseStatus.Active && p.Lease.EndDate >= asOfDate) ||
             (p.Lease.Status == LeaseStatus.NoticeGiven && (p.Lease.MoveOutDate ?? p.Lease.EndDate) >= asOfDate)));
    }
}
