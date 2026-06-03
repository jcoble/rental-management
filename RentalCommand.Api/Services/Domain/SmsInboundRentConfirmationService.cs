using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class SmsInboundRentConfirmationService : ISmsInboundRentConfirmationService
{
    private readonly RentalCommandDbContext _db;

    public SmsInboundRentConfirmationService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<SmsInboundRentConfirmationResult> HandleAsync(
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        if (!IsYes(body))
        {
            return new SmsInboundRentConfirmationResult(
                false,
                null,
                "Reply YES to confirm your rent payment was received.");
        }

        var normalizedFrom = NormalizePhone(fromPhone);
        if (string.IsNullOrWhiteSpace(normalizedFrom))
        {
            return new SmsInboundRentConfirmationResult(
                false,
                null,
                "We could not read your phone number. Please contact your property manager.");
        }

        var tenants = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.DeletedAt == null && t.Phone != null)
            .Select(t => new { t.Id, t.PortfolioId, t.Phone })
            .ToListAsync(ct);

        var tenantIds = tenants
            .Where(t => NormalizePhone(t.Phone) == normalizedFrom)
            .Select(t => t.Id)
            .ToHashSet();

        if (tenantIds.Count == 0)
        {
            return new SmsInboundRentConfirmationResult(
                false,
                null,
                "We could not match this phone number to a tenant account.");
        }

        var payment = await _db.Payments
            .Include(p => p.Lease)
            .Where(p =>
                p.PaymentType == PaymentType.Rent &&
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Late ||
                 p.Status == PaymentStatus.Partial) &&
                p.Lease != null &&
                tenantIds.Contains(p.Lease.TenantId))
            .OrderBy(p => p.DueDate)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (payment is null)
        {
            return new SmsInboundRentConfirmationResult(
                false,
                null,
                "We did not find an unpaid rent item for your account.");
        }

        payment.Status = PaymentStatus.Paid;
        payment.PaidDate = DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc);
        payment.Method = "SMS confirmation";
        payment.ExternalReference = normalizedFrom;
        payment.UpdatedAt = DateTime.UtcNow;
        payment.Notes = AppendNote(payment.Notes, $"Rent marked paid from YES SMS reply at {payment.PaidDate:O}.");

        await _db.SaveChangesAsync(ct);

        return new SmsInboundRentConfirmationResult(
            true,
            payment.Id,
            $"Thanks. We recorded your {payment.Amount:C} rent payment.");
    }

    private static bool IsYes(string? body)
    {
        var normalized = body?.Trim().Trim('.', '!', '?').ToUpperInvariant();
        return normalized is "Y" or "YES";
    }

    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits.StartsWith('1')) return "+" + digits;
        return digits.Length > 0 ? "+" + digits : string.Empty;
    }

    private static string AppendNote(string? current, string note)
        => string.IsNullOrWhiteSpace(current) ? note : current.Trim() + Environment.NewLine + note;
}
