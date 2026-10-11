using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed record AtomicGuidedTenantSetupCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicGuidedTenantSetupResult(
    string TenantsJson);

/// <summary>
/// Owns the complete tenant step in Guided Setup. The reviewed request, every Tenant row, every
/// semantic audit, every realtime update, and the command receipt share one kernel transaction.
/// </summary>
public sealed class AtomicGuidedTenantSetupRule
{
    private readonly RentalCommandDbContext _db;

    public AtomicGuidedTenantSetupRule(RentalCommandDbContext db) => _db = db;

    private const int MaximumBatchSize = 25;

    public async Task<AtomicGuidedTenantSetupResult> ExecuteAsync(
        AtomicGuidedTenantSetupCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        ValidateCommand(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or no longer permits tenant onboarding. Refresh and try again.");
        }

        var requests = ReadAndValidateRequests(command);
        var tenants = requests.Select(request => new Tenant
        {
            PortfolioId = command.PortfolioId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone,
            EmergencyContact = request.EmergencyContact,
            DateOfBirth = request.DateOfBirth,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToArray();

        attempt.UseDatabaseWallClockForAudit(now);
        _db.AddRange(tenants);
        foreach (var tenant in tenants)
        {
            attempt.BindSemanticAudit(tenant, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Tenant),
                0,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: $"Tenant {tenant.FirstName} {tenant.LastName} created through Guided Setup"));
        }

        await attempt.FlushBusinessAsync(ct);

        for (var index = 0; index < tenants.Length; index++)
        {
            var tenant = tenants[index];
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Tenant),
                    entityId = tenant.Id,
                    operation = "update",
                    data = new { },
                }),
                IdempotencyKey = $"{command.DeliveryIdempotencyKey}:tenant-{index + 1}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        var response = tenants.Select(TenantResponse.FromEntity).ToArray();
        return new AtomicGuidedTenantSetupResult(JsonSerializer.Serialize(response));
    }

    public async Task AuthorizeReplayAsync(
        AtomicGuidedTenantSetupCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateCommand(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or no longer permits tenant onboarding. Refresh and try again.");
        }
    }

    private Task<bool> IsAuthorizedAsync(
        AtomicGuidedTenantSetupCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
            && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.SecurityManage
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Workspace), ct);

    private IReadOnlyList<CreateTenantRequest> ReadAndValidateRequests(
        AtomicGuidedTenantSetupCommand command)
    {
        var envelope = JsonSerializer.Deserialize<GuidedTenantSetupRequest>(command.RequestJson)
            ?? throw new DomainValidationException("Tenant setup details are required.");
        if (envelope.Tenants.Count is < 1 or > MaximumBatchSize)
        {
            throw new DomainValidationException(
                $"Add between 1 and {MaximumBatchSize} tenants in one Guided Setup step.");
        }

        var normalized = new List<CreateTenantRequest>(envelope.Tenants.Count);
        foreach (var request in envelope.Tenants)
        {
            if (request is null)
                throw new DomainValidationException("Every tenant row must contain tenant details.");

            var candidate = new CreateTenantRequest
            {
                FirstName = request.FirstName?.Trim() ?? string.Empty,
                LastName = request.LastName?.Trim() ?? string.Empty,
                Email = Normalize(request.Email),
                Phone = Normalize(request.Phone),
                EmergencyContact = Normalize(request.EmergencyContact),
                DateOfBirth = Utc(request.DateOfBirth),
                Notes = Normalize(request.Notes),
            };
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(
                    candidate, new ValidationContext(candidate), errors, validateAllProperties: true))
            {
                throw new DomainValidationException(
                    errors.FirstOrDefault()?.ErrorMessage ?? "Review the tenant details and try again.");
            }

            normalized.Add(candidate);
        }

        return normalized;
    }

    private void ValidateCommand(AtomicGuidedTenantSetupCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Portfolio, actor, access revision, tenant details, and delivery key are required.");
        }
    }

    private string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DateTime? Utc(DateTime? value) => value is null
        ? null
        : value.Value.Kind == DateTimeKind.Utc
            ? value
            : value.Value.ToUniversalTime();
}

public static class AtomicGuidedTenantSetup
{
    public static readonly AtomicJsonResultCodec<AtomicGuidedTenantSetupResult> Codec =
        new("rental.guided-tenant-setup.v1");

    public static TransactionalWrite<AtomicGuidedTenantSetupCommand, AtomicGuidedTenantSetupResult> Write(
        RentalCommandDbContext db,
        AtomicGuidedTenantSetupCommand command)
    {
        var handler = new AtomicGuidedTenantSetupRule(db);
        return new TransactionalWrite<AtomicGuidedTenantSetupCommand, AtomicGuidedTenantSetupResult>(
            Identity(command).CommandType,  command, Codec.ContractName,
            WriteLockPlan.None, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static AtomicGuidedTenantSetupCommand Command(
        WorkspaceReadScope scope,
        GuidedTenantSetupRequest request,
        string operationKey) => new(
        scope.PortfolioId,
        scope.UserId,
        scope.SessionId,
        scope.AccessContextId,
        scope.AccessRevision,
        JsonSerializer.Serialize(request),
        operationKey);

    public static AtomicCommandIdentity Identity(AtomicGuidedTenantSetupCommand command) => new(
        "rental.tenant.guided-setup",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.DeliveryIdempotencyKey}");
}
