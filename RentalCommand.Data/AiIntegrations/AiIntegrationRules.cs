using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.AiIntegrations;

public static class AiIntegrationWriteSupport
{
    public static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        RentalCommandDbContext db,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        object write = command switch
        {
            ActivateWorkspaceLlmCredentialCommand value => Build(
                "ai.integration.credential.activate", "ai.integration.status.v1", value,
                new ActivateWorkspaceLlmCredentialRule(db).ExecuteAsync,
                new ActivateWorkspaceLlmCredentialRule(db).AuthorizeReplayAsync),
            RotateWorkspaceLlmCredentialCommand value => Build(
                "ai.integration.credential.rotate", "ai.integration.status.v1", value,
                new RotateWorkspaceLlmCredentialRule(db).ExecuteAsync,
                new RotateWorkspaceLlmCredentialRule(db).AuthorizeReplayAsync),
            RemoveWorkspaceLlmCredentialCommand value => Build(
                "ai.integration.credential.remove", "ai.integration.remove.v1", value,
                new RemoveWorkspaceLlmCredentialRule(db).ExecuteAsync,
                new RemoveWorkspaceLlmCredentialRule(db).AuthorizeReplayAsync),
            RecordLlmUsageEvidenceCommand value => Build(
                "ai.integration.usage.record", "ai.integration.usage.v1", value,
                new RecordLlmUsageEvidenceRule(db).ExecuteAsync,
                new RecordLlmUsageEvidenceRule(db).AuthorizeReplayAsync),
            PortfolioQaDeliveryCommand value => Build(
                "portfolio.qa.delivery", "portfolio.qa.delivery.v1", value,
                new PortfolioQaDeliveryRule(db).ExecuteAsync,
                new PortfolioQaDeliveryRule(db).AuthorizeReplayAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return (TransactionalWrite<TCommand, TResult>)write;
    }

    private static TransactionalWrite<TCommand, TResult> Build<TCommand, TResult>(
        string operationName,
        string resultContract,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull => new(
            operationName,  command, resultContract,
            WriteLockPlan.None, executeAsync, authorizeReplayAsync);

}

public sealed class ActivateWorkspaceLlmCredentialRule
{
    private readonly RentalCommandDbContext _db;

    public ActivateWorkspaceLlmCredentialRule(RentalCommandDbContext db) => _db = db;

    public async Task<AiIntegrationStatusResult> ExecuteAsync(
        ActivateWorkspaceLlmCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateActor(command.PortfolioId, command);
        await AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            context,
            ct);
        await context.AcquireLockAsync("WorkspaceLlmCredential", command.PortfolioId, ct);
        var row = await _db.Set<WorkspaceLlmCredential>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId, ct);
        var operation = row is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (row is null)
        {
            row = new WorkspaceLlmCredential
            {
                PortfolioId = command.PortfolioId,
                CreatedAtUtc = command.TestedAtUtc,
            };
            _db.Add(row);
        }

        row.Provider = command.Provider;
        row.ModelId = command.ModelId;
        row.ApiKeyCipherText = command.ApiKeyCipherText;
        row.LastTestedAtUtc = command.TestedAtUtc;
        row.UpdatedAtUtc = command.TestedAtUtc;
        context.BindSemanticAudit(row, AiIntegrationCommandSupport.CredentialAudit(
            command.PortfolioId,
            command.ActorUserId,
            operation,
            command.Provider,
            command.ModelId,
            operation == AuditLogOperation.Created
                ? "Activated workspace LLM credential."
                : "Updated workspace LLM credential."));
        return MapStatus(row);
    }

    public Task AuthorizeReplayAsync(
        ActivateWorkspaceLlmCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityReplayAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            ct);

    internal static AiIntegrationStatusResult MapStatus(WorkspaceLlmCredential row) =>
        new(true, row.Provider, row.ModelId, row.LastTestedAtUtc, row.UpdatedAtUtc);
}

public sealed class RotateWorkspaceLlmCredentialRule
{
    private readonly RentalCommandDbContext _db;

    public RotateWorkspaceLlmCredentialRule(RentalCommandDbContext db) => _db = db;

    public async Task<AiIntegrationStatusResult> ExecuteAsync(
        RotateWorkspaceLlmCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateActor(command.PortfolioId, command);
        await AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            context,
            ct);
        await context.AcquireLockAsync("WorkspaceLlmCredential", command.PortfolioId, ct);
        var row = await _db.Set<WorkspaceLlmCredential>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId, ct)
            ?? throw new InvalidOperationException(
                "No AI provider is configured. Activate a credential before rotating it.");

        row.Provider = command.Provider;
        row.ModelId = command.ModelId;
        row.ApiKeyCipherText = command.ApiKeyCipherText;
        row.LastTestedAtUtc = command.TestedAtUtc;
        row.UpdatedAtUtc = command.TestedAtUtc;
        row.RotatedAtUtc = command.TestedAtUtc;
        context.BindSemanticAudit(row, AiIntegrationCommandSupport.CredentialAudit(
            command.PortfolioId,
            command.ActorUserId,
            AuditLogOperation.Updated,
            command.Provider,
            command.ModelId,
            "Rotated workspace LLM credential."));
        return ActivateWorkspaceLlmCredentialRule.MapStatus(row);
    }

    public Task AuthorizeReplayAsync(
        RotateWorkspaceLlmCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityReplayAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            ct);
}

public sealed class RemoveWorkspaceLlmCredentialRule
{
    private readonly RentalCommandDbContext _db;

    public RemoveWorkspaceLlmCredentialRule(RentalCommandDbContext db) => _db = db;

    public async Task<RemoveWorkspaceLlmCredentialResult> ExecuteAsync(
        RemoveWorkspaceLlmCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateActor(command.PortfolioId, command);
        await AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            context,
            ct);
        await context.AcquireLockAsync("WorkspaceLlmCredential", command.PortfolioId, ct);
        var row = await _db.Set<WorkspaceLlmCredential>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId, ct);
        if (row is null)
        {
            throw new InvalidOperationException("No AI provider credential is configured.");
        }

        _db.Remove(row);
        context.BindSemanticAudit(row, AiIntegrationCommandSupport.CredentialAudit(
            command.PortfolioId,
            command.ActorUserId,
            AuditLogOperation.Deleted,
            row.Provider,
            row.ModelId,
            "Removed workspace LLM credential."));
        return new RemoveWorkspaceLlmCredentialResult(true);
    }

    public Task AuthorizeReplayAsync(
        RemoveWorkspaceLlmCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AiIntegrationCommandSupport.AuthorizeWorkspaceCapabilityReplayAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.IntegrationsManage,
            _db,
            ct);
}

public sealed class RecordLlmUsageEvidenceRule
{
    private readonly RentalCommandDbContext _db;

    public RecordLlmUsageEvidenceRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecordLlmUsageEvidenceResult> ExecuteAsync(
        RecordLlmUsageEvidenceCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateUsage(command);
        await context.AcquireLockAsync(
            "LlmUsageEvidence",
            StableUsageEventKey(command.PortfolioId, command.UsageEventIdentity),
            ct);

        var evidence = new LlmUsageEvidence
        {
            PortfolioId = command.PortfolioId,
            Provider = command.Provider,
            ModelId = command.ModelId,
            Feature = command.Feature,
            LatencyMilliseconds = command.LatencyMilliseconds,
            InputUnits = command.InputUnits,
            OutputUnits = command.OutputUnits,
            EstimatedCostUsd = command.EstimatedCostUsd,
            OccurredAtUtc = command.OccurredAtUtc,
        };
        _db.Add(evidence);
        await context.FlushBusinessAsync(ct);
        return new RecordLlmUsageEvidenceResult(evidence.Id);
    }

    public async Task AuthorizeReplayAsync(
        RecordLlmUsageEvidenceCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateUsage(command);

        var evidenceExists = await _db.Set<LlmUsageEvidence>()
            .AsNoTracking()
            .AnyAsync(evidence =>
                evidence.PortfolioId == command.PortfolioId &&
                evidence.Provider == command.Provider &&
                evidence.ModelId == command.ModelId &&
                evidence.Feature == command.Feature &&
                evidence.OccurredAtUtc == command.OccurredAtUtc,
                ct);
        if (!evidenceExists)
        {
            throw new UnauthorizedAccessException("The LLM usage evidence row is unavailable.");
        }
    }

    private static Guid StableUsageEventKey(int portfolioId, string usageEventIdentity)
    {
        var raw = $"llm-usage:{portfolioId}:{usageEventIdentity}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(raw), hash);
        return new Guid(hash[..16]);
    }
}

public sealed class PortfolioQaDeliveryRule
{
    private readonly RentalCommandDbContext _db;

    public PortfolioQaDeliveryRule(RentalCommandDbContext db) => _db = db;

    public async Task<PortfolioQaDeliveryResult> ExecuteAsync(
        PortfolioQaDeliveryCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        AiIntegrationCommandSupport.ValidateActor(command.PortfolioId, command);
        await AiIntegrationCommandSupport.AuthorizePropertyCapabilityAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.ReportsRead,
            _db,
            context,
            ct);

        var queued = new List<string>();
        var subject = "Your Rental Command answer";
        var body = $"You asked:\n{command.Question}\n\nAnswer:\n{command.Answer}";

        if (!string.IsNullOrWhiteSpace(command.ToEmail))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    to = command.ToEmail.Trim(),
                    subject,
                    body,
                }),
                IdempotencyKey = command.EmailDeliveryIdempotencyKey,
                CreatedAtUtc = command.CreatedAtUtc,
                NextAttemptAtUtc = command.CreatedAtUtc,
            });
            queued.Add("Email");
        }

        if (!string.IsNullOrWhiteSpace(command.ToSms))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new
                {
                    to = command.ToSms.Trim(),
                    message = body,
                }),
                IdempotencyKey = command.SmsDeliveryIdempotencyKey,
                CreatedAtUtc = command.CreatedAtUtc,
                NextAttemptAtUtc = command.CreatedAtUtc,
            });
            queued.Add("Sms");
        }

        return new PortfolioQaDeliveryResult(queued);
    }

    public Task AuthorizeReplayAsync(
        PortfolioQaDeliveryCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        AiIntegrationCommandSupport.AuthorizePropertyCapabilityReplayAsync(
            command.PortfolioId,
            command,
            CapabilityKeys.ReportsRead,
            _db,
            ct);
}

internal static class AiIntegrationCommandSupport
{
    public static void ValidateActor(int portfolioId, IAiIntegrationActorCommand command)
    {
        if (portfolioId <= 0 || command.ActorUserId <= 0 || command.ActorAuthSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0)
        {
            throw new UnauthorizedAccessException("A current workspace access envelope is required.");
        }
    }

    public static void ValidateUsage(RecordLlmUsageEvidenceCommand command)
    {
        if (command.PortfolioId <= 0 ||
            string.IsNullOrWhiteSpace(command.UsageEventIdentity) ||
            string.IsNullOrWhiteSpace(command.Provider) ||
            string.IsNullOrWhiteSpace(command.ModelId) ||
            string.IsNullOrWhiteSpace(command.Feature) ||
            command.UsageEventIdentity.Length > 200 ||
            command.Provider.Length > 32 ||
            command.ModelId.Length > 128 ||
            command.Feature.Length > 80 ||
            command.LatencyMilliseconds < 0 ||
            command.InputUnits < 0 ||
            command.OutputUnits < 0 ||
            command.EstimatedCostUsd < 0m)
        {
            throw new ArgumentException("LLM usage evidence contains invalid identity or amount values.");
        }
    }

    public static async Task AuthorizeWorkspaceCapabilityAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("WorkspaceAccessContext", command.ActorAccessContextId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        if (!await HasWorkspaceCapabilityAsync(
                portfolioId, command, capability, db, now, ct))
        {
            throw new UnauthorizedAccessException("The active workspace assignment cannot manage integrations.");
        }
    }

    public static async Task AuthorizeWorkspaceCapabilityReplayAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        ValidateActor(portfolioId, command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await HasWorkspaceCapabilityAsync(portfolioId, command, capability, db, now, ct))
        {
            throw new UnauthorizedAccessException("The active workspace assignment cannot replay this integration command.");
        }
    }

    public static async Task AuthorizePropertyCapabilityAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("WorkspaceAccessContext", command.ActorAccessContextId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await HasPropertyCapabilityAsync(portfolioId, command, capability, db, now, ct))
        {
            throw new UnauthorizedAccessException("The active workspace assignment cannot read portfolio reports.");
        }
    }

    public static async Task AuthorizePropertyCapabilityReplayAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        ValidateActor(portfolioId, command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await HasPropertyCapabilityAsync(portfolioId, command, capability, db, now, ct))
        {
            throw new UnauthorizedAccessException("The active workspace assignment cannot replay this Q&A delivery command.");
        }
    }

    public static AtomicSemanticAudit CredentialAudit(
        int portfolioId,
        int actorUserId,
        AuditLogOperation operation,
        string provider,
        string modelId,
        string reason) =>
        new(
            portfolioId,
            nameof(WorkspaceLlmCredential),
            0,
            operation,
            actorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                Provider = provider,
                ModelId = modelId,
            }),
            ChangeReason: reason);

    private static Task<bool> HasWorkspaceCapabilityAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == portfolioId &&
            assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.Id == command.ActorAccessContextId &&
            assignment.WorkspaceMembership.AccessContext.UserId == command.ActorUserId &&
            assignment.WorkspaceMembership.AccessContext.PortfolioId == portfolioId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == command.ActorAccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= now &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > now) &&
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= now &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
            assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == capability &&
                grant.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace) &&
            db.Set<AuthSession>().Any(session =>
                session.Id == command.ActorAuthSessionId &&
                session.UserId == command.ActorUserId &&
                session.ActiveAccessContextId == command.ActorAccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > now),
            ct);

    private static Task<bool> HasPropertyCapabilityAsync(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        PropertyCapabilityAssignments(portfolioId, command, capability, db, now).AnyAsync(ct);

    internal static IQueryable<MembershipRoleAssignment> PropertyCapabilityAssignments(
        int portfolioId,
        IAiIntegrationActorCommand command,
        string capability,
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.AuthorizedAssignmentsForScope(
            new WorkspaceReadScope(
                portfolioId,
                command.ActorUserId,
                command.ActorAuthSessionId,
                command.ActorAccessContextId,
                command.ActorAccessRevision),
            [capability],
            CapabilityAuthorizationTargetKind.Property,
            now);
        return assignments.Where(assignment =>
            (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
             (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
              assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId))));
    }
}
