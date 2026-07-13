using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Authorization;
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
        var authorizedAssignments = attempt.Persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId &&
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= command.RequestedAtUtc &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > command.RequestedAtUtc) &&
            assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContextId == command.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= command.RequestedAtUtc &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > command.RequestedAtUtc) &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.UserId == command.ActorUserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == command.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            attempt.Persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId &&
                session.UserId == command.ActorUserId &&
                session.ActiveAccessContextId == command.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > command.RequestedAtUtc) &&
            assignment.RoleProfile != null &&
            assignment.RoleProfile.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition != null &&
                profileCapability.CapabilityDefinition.Key == CapabilityKeys.WorkManage &&
                profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));

        // Vendor scope, portfolio existence, destination eligibility, and the message labels are
        // resolved by one translated SQL statement. No load-then-filter or follow-up query.
        var target = await (
            from vendor in attempt.Persistence.Query<Vendor>()
            join portfolio in attempt.Persistence.Query<Portfolio>()
                on vendor.PortfolioId equals portfolio.Id
            where vendor.Id == command.VendorId
                && vendor.PortfolioId == command.PortfolioId
                && authorizedAssignments.Any()
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
