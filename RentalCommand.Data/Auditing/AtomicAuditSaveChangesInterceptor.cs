using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic; // Separate audit interceptor, shared by normal and atomic saves.

/// <summary>
/// Opt-in atomic-kernel interceptor. It stages exact audit rows outside EF's tracker and never calls
/// SaveChanges recursively; the executor materializes them only in its final companion flush.
/// </summary>
internal sealed class AtomicAuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly AtomicAuditScope _scope;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingAudit> _pending = [];

    public AtomicAuditSaveChangesInterceptor(
        AtomicAuditScope scope,
        ICurrentActor actor,
        TimeProvider timeProvider)
    {
        _scope = scope;
        _actor = actor;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Stage();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Stage();
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _pending.Clear();

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        _pending.Clear();
        if (context is null)
        {
            return;
        }

        _scope.GuardSaveChanges(context);
        if (!_scope.IsActive)
        {
            return;
        }

        var auditable = context.ChangeTracker.Entries()
            .Where(entry => (entry.Entity is IAuditable and IPortfolioScoped
                    || entry.Entity is Portfolio)
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();
        foreach (var entry in auditable)
        {
            var operation = Operation(entry);
            var modified = entry.Properties
                .Where(property => property.IsModified && !property.Metadata.IsPrimaryKey())
                .ToArray();
            if (operation == AuditLogOperation.Updated && modified.Length == 0)
            {
                continue;
            }

            var oldValues = operation == AuditLogOperation.Created
                ? null
                : Serialize(operation == AuditLogOperation.Updated ? modified : entry.Properties, true);
            var newValues = operation == AuditLogOperation.Deleted
                ? null
                : Serialize(operation == AuditLogOperation.Updated ? modified : entry.Properties, false);
            _pending.Add(new PendingAudit(
                entry,
                entry.Entity.GetType().Name,
                entry.Entity is Portfolio portfolio
                    ? portfolio.Id
                    : ((IPortfolioScoped)entry.Entity).PortfolioId,
                operation,
                oldValues,
                newValues));
        }
    }

    private void Stage()
    {
        if (!_scope.IsActive)
        {
            _pending.Clear();
            return;
        }

        var timestamp = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var pending in _pending)
        {
            _scope.StageTrackedMutation(
                pending.Entry.Entity,
                pending.EntityType,
                ReadEntityId(pending.Entry),
                pending.PortfolioId,
                pending.Operation,
                pending.OldValues,
                pending.NewValues,
                _actor.UserId,
                _actor.ActorLabel,
                _actor.IpAddress,
                timestamp);
        }

        _pending.Clear();
    }

    private static AuditLogOperation Operation(EntityEntry entry)
    {
        if (entry.State == EntityState.Modified
            && entry.Metadata.FindProperty("DeletedAt") is not null)
        {
            var deletedAt = entry.Property("DeletedAt");
            if (deletedAt.IsModified && deletedAt.OriginalValue is null && deletedAt.CurrentValue is not null)
            {
                return AuditLogOperation.Deleted;
            }
        }

        return entry.State switch
        {
            EntityState.Added => AuditLogOperation.Created,
            EntityState.Modified => AuditLogOperation.Updated,
            EntityState.Deleted => AuditLogOperation.Deleted,
            _ => throw new InvalidOperationException("Entry is not an auditable mutation."),
        };
    }

    private static int ReadEntityId(EntityEntry entry)
    {
        var primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey?.Properties.Count != 1)
        {
            throw new AtomicArchitectureException(
                $"Atomic audit requires a single-column key for {entry.Entity.GetType().Name}.");
        }

        return Convert.ToInt32(entry.Property(primaryKey.Properties[0].Name).CurrentValue);
    }

    private static string Serialize(IEnumerable<PropertyEntry> properties, bool original)
    {
        var values = properties.ToDictionary(
            property => property.Metadata.Name,
            property => original ? property.OriginalValue : property.CurrentValue);
        return JsonSerializer.Serialize(values);
    }

    private sealed record PendingAudit(
        EntityEntry Entry,
        string EntityType,
        int PortfolioId,
        AuditLogOperation Operation,
        string? OldValues,
        string? NewValues);
}
