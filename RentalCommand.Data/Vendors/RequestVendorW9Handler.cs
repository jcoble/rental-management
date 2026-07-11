using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Vendors;

namespace RentalCommand.Data.Vendors;

public sealed class RequestVendorW9Handler
    : IAtomicCommandHandler<RequestVendorW9Command, RequestVendorW9Result>
{
    public async Task<RequestVendorW9Result> HandleAsync(
        RequestVendorW9Command command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        // Vendor scope, portfolio existence, destination eligibility, and the message labels are
        // resolved by one translated SQL statement. No load-then-filter or follow-up query.
        var target = await (
            from vendor in attempt.Persistence.Query<Vendor>()
            join portfolio in attempt.Persistence.Query<Portfolio>()
                on vendor.PortfolioId equals portfolio.Id
            where vendor.Id == command.VendorId
                && vendor.PortfolioId == command.PortfolioId
            select new
            {
                vendor.Id,
                vendor.Name,
                vendor.NormalizedPhone,
                portfolio.ManagementCompanyName,
            })
            .TagWith("vendor-w9.target-eligibility")
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            return new RequestVendorW9Result(RequestVendorW9Outcome.NotFound, null);
        }

        if (string.IsNullOrWhiteSpace(target.NormalizedPhone))
        {
            return new RequestVendorW9Result(RequestVendorW9Outcome.VendorHasNoPhone, null);
        }

        var message = BuildW9RequestSms(target.Name, target.ManagementCompanyName);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Vendor),
            target.Id,
            AuditLogOperation.Updated,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
            NewValues: JsonSerializer.Serialize(new
            {
                w9Requested = true,
                to = target.NormalizedPhone,
                command.ClientOperationId,
            }),
            ChangeReason: $"Texted a W-9 request to vendor {target.Name} for 1099 tax reporting."));
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "sms",
            Payload = JsonSerializer.Serialize(new
            {
                to = target.NormalizedPhone,
                message,
            }),
            // A client operation is one deliberate user action. Transport retries reuse it and
            // therefore cannot create a second delivery; a new operation intentionally requests
            // another text.
            IdempotencyKey = OutboxIdempotency.Create(
                "vendor-w9",
                command.PortfolioId,
                target.Id,
                command.ClientOperationId),
            CreatedAtUtc = command.RequestedAtUtc,
            NextAttemptAtUtc = command.RequestedAtUtc,
        });

        return new RequestVendorW9Result(RequestVendorW9Outcome.Queued, target.NormalizedPhone);
    }

    private static string BuildW9RequestSms(string vendorName, string? companyName)
    {
        var company = string.IsNullOrWhiteSpace(companyName) ? "our office" : companyName.Trim();
        var greeting = string.IsNullOrWhiteSpace(vendorName) ? "Hi," : $"Hi {vendorName.Trim()},";
        return $"{greeting} this is {company}. For our 1099 tax filing, could you please send us a " +
               "completed W-9 form (it has your business name and tax ID)? You can reply to this text " +
               "with a photo of it or email it over. Thanks so much!";
    }
}
