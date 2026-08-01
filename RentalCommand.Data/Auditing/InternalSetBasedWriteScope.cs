using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalCommand.Core.Atomic;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Atomic; // Generated-infrastructure audit scope.

internal interface IInternalSetBasedWriteScope
{
    IDisposable BeginWrite(string tableName, InternalWriteOperation operation);
}

internal enum InternalWriteOperation
{
    Insert,
    Update,
    Delete,
}

internal sealed class InternalSetBasedWriteScope : IInternalSetBasedWriteScope
{
    private readonly AtomicAuditScope _auditScope;

    public InternalSetBasedWriteScope(AtomicAuditScope auditScope) =>
        _auditScope = auditScope;

    public IDisposable BeginWrite(string tableName, InternalWriteOperation operation) =>
        _auditScope.BeginInternalWrite(tableName, operation);
}

public static class GeneratedInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPendingFileUploadStore(this IServiceCollection services)
    {
        services.TryAddScoped<IInternalSetBasedWriteScope, InternalSetBasedWriteScope>();
        services.TryAddScoped<IPendingFileUploadStore>(provider => new PendingFileUploadStore(
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IInternalSetBasedWriteScope>()));
        return services;
    }

    public static IServiceCollection AddGeneratedInfrastructureStores(this IServiceCollection services)
    {
        services.TryAddScoped<IInternalSetBasedWriteScope, InternalSetBasedWriteScope>();
        services.TryAddScoped<ITenantNoticeWorkClaimStore>(provider => new TenantNoticeWorkClaimStore(
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IInternalSetBasedWriteScope>()));
        services.TryAddScoped<ITenantNoticeCandidateStore>(provider => new TenantNoticeCandidateStore(
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IInternalSetBasedWriteScope>()));
        services.TryAddScoped<IEngineWorkerHeartbeatStore, EngineWorkerHeartbeatStore>();
        return services;
    }
}
