using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Vendors;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Vendors;

public sealed class RequestVendorW9Rule
{
    public const string ResultContract = "vendor-w9.request.result.v1";

    private readonly RentalCommandDbContext _db;

    public RequestVendorW9Rule(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<RequestVendorW9Command, RequestVendorW9Result> Write(
        RequestVendorW9Command command,
        RentalCommandDbContext db)
    {
        var handler = new RequestVendorW9Rule(db);
        return new TransactionalWrite<RequestVendorW9Command, RequestVendorW9Result>(
            "vendor-w9.request",  command, ResultContract,
            WriteLockPlan.None, handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<RequestVendorW9Result> ExecuteAsync(
        RequestVendorW9Command command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var securityAtUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var authorizedVendors = AuthorizedVendors(command, _db, securityAtUtc);

        // Vendor scope, portfolio existence, destination eligibility, and the message labels are
        // resolved by one translated SQL statement. No load-then-filter or follow-up query.
        var target = await (
            from vendor in authorizedVendors
            join portfolio in _db.Set<Portfolio>()
                on vendor.PortfolioId equals portfolio.Id
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
        context.StageSemanticEvent(new AtomicSemanticAudit(
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
        context.StageOutbox(new OutboxMessage
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

    public async Task AuthorizeAsync(
        RequestVendorW9Command command, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityAtUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var authorized = await AuthorizedVendors(command, _db, securityAtUtc)
            .TagWith("vendor-w9.replay-authorization")
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The active assignment cannot replay this vendor W-9 request.");
        }
    }

    internal static IQueryable<Vendor> AuthorizedVendors(
        RequestVendorW9Command command,
        RentalCommandDbContext db,
        DateTime securityAtUtc)
    {
        var scope = new WorkspaceReadScope(
            command.PortfolioId,
            command.ActorUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.AccessRevision);
        var assignments = db.AuthorizedAssignmentsForScope(
                scope,
                [CapabilityKeys.WorkManage],
                CapabilityAuthorizationTargetKind.Property,
                securityAtUtc)
            .Where(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        return db.Set<Vendor>().AsNoTracking().Where(vendor =>
            vendor.Id == command.VendorId
            && vendor.PortfolioId == command.PortfolioId
            && assignments.Any());
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
