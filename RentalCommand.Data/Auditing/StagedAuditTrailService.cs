using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Stages semantic audit information in the active atomic attempt. This service intentionally has
/// no DbContext dependency and never calls SaveChanges, so it cannot flush unrelated tracked state.
/// </summary>
public class StagedAuditTrailService : IAuditTrailService
{
    private readonly IAuditScope _scope;
    private readonly TimeProvider _timeProvider;

    public StagedAuditTrailService(IAuditScope scope, TimeProvider timeProvider)
    {
        _scope = scope;
        _timeProvider = timeProvider;
    }

    public Task LogAsync(
        int portfolioId,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        int? userId = null,
        string? actorLabel = null,
        string? oldValues = null,
        string? newValues = null,
        string? changeReason = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resolution = _scope.ResolveExplicit(entityType, entityId, operation);
        if (resolution.Decision == AuditWrite.Enrich)
        {
            var existing = resolution.ExistingRow
                ?? throw new InvalidOperationException(
                    $"Audit mutation {resolution.MutationOrdinal} has no generic row to enrich.");
            if (oldValues is not null) existing.OldValues = oldValues;
            if (newValues is not null) existing.NewValues = newValues;
            if (changeReason is not null) existing.ChangeReason = changeReason;
            if (userId.HasValue) existing.UserId = userId;
            if (actorLabel is not null) existing.ActorLabel = actorLabel;
            if (ipAddress is not null) existing.IpAddress = ipAddress;
            return Task.CompletedTask;
        }

        _scope.StageExplicit(resolution.MutationOrdinal, new AuditLog
        {
            PortfolioId = portfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = operation,
            UserId = userId,
            ActorLabel = actorLabel,
            OldValues = oldValues,
            NewValues = newValues,
            ChangeReason = changeReason,
            IpAddress = ipAddress,
            Timestamp = _timeProvider.UtcNow(),
        });
        return Task.CompletedTask;
    }
}
