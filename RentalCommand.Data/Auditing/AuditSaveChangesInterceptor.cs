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
/// Auto-records create / edit / delete on every <see cref="IAuditable"/> business entity into the
/// append-only <see cref="AuditLog"/>, so the audit trail fills up without each service having to
/// remember to call <c>AuditTrailService.LogAsync</c>. The rich explicit log still wins for the
/// legally-sensitive semantic events — they coordinate via <see cref="IAuditScope"/> so a single
/// change is never recorded twice.
///
/// <para>Mechanics: <c>SavingChanges</c> snapshots each pending audit (the PK of an inserted row is
/// not yet assigned). <c>SavedChanges</c> reads the now-populated PKs, claims each key in the
/// scope, and writes the surviving <see cref="AuditLog"/> rows through the same context — atomic
/// with the change. A re-entrancy flag stops that second save from recursing; <see cref="AuditLog"/>
/// is not <see cref="IAuditable"/> so it is never itself audited.</para>
///
/// Scoped (one instance per <c>DbContext</c> per request), so the pending list is per-unit-of-work.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentActor _actor;
    private readonly IAuditScope _scope;
    private readonly TimeProvider _timeProvider;

    // Property names (case-insensitive substring match) whose values are redacted from the JSON.
    private static readonly string[] RedactedFragments =
    {
        "ssn", "socialsecurity", "taxid", "password", "secret", "token", "apikey",
    };

    private readonly List<PendingAudit> _pending = new();
    private bool _writing;

    public AuditSaveChangesInterceptor(ICurrentActor actor, IAuditScope scope, TimeProvider timeProvider)
    {
        _actor = actor;
        _scope = scope;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (!_writing)
        {
            CapturePending(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_writing)
        {
            CapturePending(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (!_writing)
        {
            WritePending(eventData.Context);
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (!_writing)
        {
            await WritePendingAsync(eventData.Context, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Walk the change tracker before the flush and snapshot a pending audit for each tracked
    /// <see cref="IAuditable"/> entity. The entity reference is kept so the (possibly DB-generated)
    /// PK can be read after the save.
    /// </summary>
    private void CapturePending(DbContext? context)
    {
        _pending.Clear();
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IAuditable || entry.Entity is not IPortfolioScoped scoped)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var entityType = entry.Entity.GetType().Name;
            var portfolioId = scoped.PortfolioId;

            switch (entry.State)
            {
                case EntityState.Added:
                    _pending.Add(new PendingAudit(entry, entityType, portfolioId, AuditLogOperation.Created,
                        OldValues: null, NewValues: SerializeValues(entry, useOriginal: false)));
                    break;

                case EntityState.Deleted:
                    _pending.Add(new PendingAudit(entry, entityType, portfolioId, AuditLogOperation.Deleted,
                        OldValues: SerializeValues(entry, useOriginal: true), NewValues: null));
                    break;

                case EntityState.Modified:
                    CaptureModified(entry, entityType, portfolioId);
                    break;
            }
        }
    }

    private void CaptureModified(EntityEntry entry, string entityType, int portfolioId)
    {
        // Soft-delete: a Modified entry that sets DeletedAt from null → value is recorded as Deleted.
        var deletedAt = entry.Metadata.FindProperty("DeletedAt") is null ? null : entry.Property("DeletedAt");
        var isSoftDelete = deletedAt is not null
            && deletedAt.IsModified
            && deletedAt.OriginalValue is null
            && deletedAt.CurrentValue is not null;

        var modified = entry.Properties
            .Where(p => p.IsModified && !p.Metadata.IsPrimaryKey())
            .ToList();

        if (isSoftDelete)
        {
            var old = new Dictionary<string, object?>();
            foreach (var p in modified)
            {
                old[p.Metadata.Name] = Redact(p.Metadata.Name, p.OriginalValue);
            }

            _pending.Add(new PendingAudit(entry, entityType, portfolioId, AuditLogOperation.Deleted,
                OldValues: Serialize(old), NewValues: null));
            return;
        }

        if (modified.Count == 0)
        {
            return;
        }

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        foreach (var p in modified)
        {
            oldValues[p.Metadata.Name] = Redact(p.Metadata.Name, p.OriginalValue);
            newValues[p.Metadata.Name] = Redact(p.Metadata.Name, p.CurrentValue);
        }

        _pending.Add(new PendingAudit(entry, entityType, portfolioId, AuditLogOperation.Updated,
            OldValues: Serialize(oldValues), NewValues: Serialize(newValues)));
    }

    private void WritePending(DbContext? context)
    {
        var rows = BuildRows(context);
        if (rows.Count == 0)
        {
            return;
        }

        _writing = true;
        try
        {
            context!.Set<AuditLog>().AddRange(rows);
            context.SaveChanges();
        }
        finally
        {
            _writing = false;
            _pending.Clear();
        }
    }

    private async ValueTask WritePendingAsync(DbContext? context, CancellationToken ct)
    {
        var rows = BuildRows(context);
        if (rows.Count == 0)
        {
            return;
        }

        _writing = true;
        try
        {
            await context!.Set<AuditLog>().AddRangeAsync(rows, ct);
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            _writing = false;
            _pending.Clear();
        }
    }

    /// <summary>
    /// Resolve each pending audit's now-populated PK and register its row with the scope. The scope
    /// keeps a reference so a later explicit <c>AuditTrailService.LogAsync</c> can enrich the row in
    /// place; <see cref="IAuditScope.TryAddGenericRow"/> returns <c>false</c> (and the row is dropped)
    /// when an explicit rich log already owns the key, so the trail never double-records.
    /// </summary>
    private List<AuditLog> BuildRows(DbContext? context)
    {
        var rows = new List<AuditLog>();
        if (context is null || _pending.Count == 0)
        {
            return rows;
        }

        var now = _timeProvider.UtcNow();
        foreach (var pending in _pending)
        {
            var entityId = ReadEntityId(pending.Entry);
            if (entityId == 0)
            {
                continue;
            }

            var row = new AuditLog
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
            };

            if (_scope.TryAddGenericRow(pending.EntityType, entityId, pending.Operation, row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static int ReadEntityId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return 0;
        }

        var prop = key.Properties.Count == 1 ? key.Properties[0] : null;
        if (prop is null)
        {
            return 0;
        }

        var value = entry.Property(prop.Name).CurrentValue;
        return value is int id ? id : 0;
    }

    private static string Serialize(Dictionary<string, object?> values) =>
        JsonSerializer.Serialize(values);

    /// <summary>Full scalar snapshot (current or original values), with PII redacted.</summary>
    private static string SerializeValues(EntityEntry entry, bool useOriginal)
    {
        var values = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            if (p.Metadata.IsPrimaryKey())
            {
                continue;
            }

            var raw = useOriginal ? p.OriginalValue : p.CurrentValue;
            values[p.Metadata.Name] = Redact(p.Metadata.Name, raw);
        }

        return Serialize(values);
    }

    private static object? Redact(string propertyName, object? value)
    {
        foreach (var fragment in RedactedFragments)
        {
            if (propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return value is null ? null : "***";
            }
        }

        return value;
    }

    private sealed record PendingAudit(
        EntityEntry Entry,
        string EntityType,
        int PortfolioId,
        AuditLogOperation Operation,
        string? OldValues,
        string? NewValues);
}
