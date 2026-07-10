using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Snapshots auditable changes before a business flush and stages their rows after generated keys
/// exist. It never calls SaveChanges: the owner atomic unit of work materializes every staged audit
/// row for its final companion flush.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentActor _actor;
    private readonly IAuditScope _scope;
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingAudit> _pending = [];

    private static readonly string[] RedactedFragments =
    {
        "ssn", "socialsecurity", "taxid", "password", "secret", "token", "apikey",
    };

    public AuditSaveChangesInterceptor(ICurrentActor actor, IAuditScope scope, TimeProvider timeProvider)
    {
        _actor = actor;
        _scope = scope;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CapturePending(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CapturePending(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        StagePending();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        StagePending();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _pending.Clear();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        _pending.Clear();
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    private void CapturePending(DbContext? context)
    {
        _pending.Clear();
        if (context is null)
        {
            return;
        }

        var auditableEntries = context.ChangeTracker.Entries()
            .Where(entry => entry.Entity is IAuditable and IPortfolioScoped)
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        if (auditableEntries.Length > 0 && !_scope.IsActive)
        {
            throw new InvalidOperationException(
                "Auditable writes must execute through IAtomicUnitOfWork; no atomic audit scope is active.");
        }

        foreach (var entry in auditableEntries)
        {
            var scoped = (IPortfolioScoped)entry.Entity;
            var entityType = entry.Entity.GetType().Name;
            var operation = GetOperation(entry);
            if (operation is null)
            {
                continue;
            }

            var ordinal = _scope.ReserveMutation(
                entry.Entity,
                entityType,
                ReadEntityId(entry),
                operation.Value);

            switch (operation.Value)
            {
                case AuditLogOperation.Created:
                    _pending.Add(new PendingAudit(
                        ordinal,
                        entry,
                        entityType,
                        scoped.PortfolioId,
                        operation.Value,
                        OldValues: null,
                        NewValues: SerializeValues(entry, useOriginal: false)));
                    break;

                case AuditLogOperation.Deleted:
                    _pending.Add(new PendingAudit(
                        ordinal,
                        entry,
                        entityType,
                        scoped.PortfolioId,
                        operation.Value,
                        OldValues: BuildDeletedValues(entry),
                        NewValues: null));
                    break;

                default:
                    var (oldValues, newValues) = BuildModifiedValues(entry);
                    if (oldValues is not null)
                    {
                        _pending.Add(new PendingAudit(
                            ordinal,
                            entry,
                            entityType,
                            scoped.PortfolioId,
                            operation.Value,
                            oldValues,
                            newValues));
                    }
                    break;
            }
        }
    }

    private void StagePending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var now = _timeProvider.UtcNow();
        try
        {
            foreach (var pending in _pending)
            {
                var entityId = ReadEntityId(pending.Entry);
                if (entityId == 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot audit {pending.EntityType}: its generated integer key is still zero after SaveChanges.");
                }

                _scope.StageGeneric(pending.MutationOrdinal, new AuditLog
                {
                    PortfolioId = pending.PortfolioId,
                    EntityType = pending.EntityType,
                    EntityId = entityId,
                    Operation = pending.Operation,
                    OldValues = pending.OldValues,
                    NewValues = pending.NewValues,
                    UserId = _actor.UserId,
                    ActorLabel = _actor.ActorLabel,
                    IpAddress = _actor.IpAddress,
                    Timestamp = now,
                });
            }
        }
        finally
        {
            _pending.Clear();
        }
    }

    private static AuditLogOperation? GetOperation(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return AuditLogOperation.Created;
        }

        if (entry.State == EntityState.Deleted || IsSoftDelete(entry))
        {
            return AuditLogOperation.Deleted;
        }

        return entry.Properties.Any(property => property.IsModified && !property.Metadata.IsPrimaryKey())
            ? AuditLogOperation.Updated
            : null;
    }

    private static bool IsSoftDelete(EntityEntry entry)
    {
        var deletedAt = entry.Metadata.FindProperty("DeletedAt") is null
            ? null
            : entry.Property("DeletedAt");
        return deletedAt is not null
            && deletedAt.IsModified
            && deletedAt.OriginalValue is null
            && deletedAt.CurrentValue is not null;
    }

    private static string BuildDeletedValues(EntityEntry entry)
    {
        if (!IsSoftDelete(entry))
        {
            return SerializeValues(entry, useOriginal: true);
        }

        var oldValues = entry.Properties
            .Where(property => property.IsModified && !property.Metadata.IsPrimaryKey())
            .ToDictionary(
                property => property.Metadata.Name,
                property => Redact(property.Metadata.Name, property.OriginalValue));
        return Serialize(oldValues);
    }

    private static (string? OldValues, string? NewValues) BuildModifiedValues(EntityEntry entry)
    {
        var modified = entry.Properties
            .Where(property => property.IsModified && !property.Metadata.IsPrimaryKey())
            .ToArray();
        if (modified.Length == 0)
        {
            return (null, null);
        }

        var oldValues = modified.ToDictionary(
            property => property.Metadata.Name,
            property => Redact(property.Metadata.Name, property.OriginalValue));
        var newValues = modified.ToDictionary(
            property => property.Metadata.Name,
            property => Redact(property.Metadata.Name, property.CurrentValue));
        return (Serialize(oldValues), Serialize(newValues));
    }

    private static int ReadEntityId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key?.Properties.Count != 1)
        {
            return 0;
        }

        return entry.Property(key.Properties[0].Name).CurrentValue is int id ? id : 0;
    }

    private static string SerializeValues(EntityEntry entry, bool useOriginal)
    {
        var values = entry.Properties
            .Where(property => !property.Metadata.IsPrimaryKey())
            .ToDictionary(
                property => property.Metadata.Name,
                property => Redact(
                    property.Metadata.Name,
                    useOriginal ? property.OriginalValue : property.CurrentValue));
        return Serialize(values);
    }

    private static string Serialize(Dictionary<string, object?> values) =>
        JsonSerializer.Serialize(values);

    private static object? Redact(string propertyName, object? value) =>
        RedactedFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            ? value is null ? null : "***"
            : value;

    private sealed record PendingAudit(
        long MutationOrdinal,
        EntityEntry Entry,
        string EntityType,
        int PortfolioId,
        AuditLogOperation Operation,
        string? OldValues,
        string? NewValues);
}
