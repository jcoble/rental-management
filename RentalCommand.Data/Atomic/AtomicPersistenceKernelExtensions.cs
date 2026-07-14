using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Deliberate opt-in registration for converted write paths. The existing application does not call
/// either extension, so its legacy audit/write behavior remains unchanged during migration.
/// </summary>
public static class AtomicPersistenceKernelExtensions
{
    public static IServiceCollection AddAtomicCommandHandler<TCommand, TResult, THandler>(
        this IServiceCollection services)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
        where THandler : class, IAtomicCommandHandler<TCommand, TResult>
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(AtomicHandlerRegistration<TCommand, TResult>)))
        {
            throw new InvalidOperationException(
                $"An atomic handler is already registered for {typeof(TCommand).Name}/{typeof(TResult).Name}.");
        }

        services.AddSingleton(new AtomicHandlerRegistration<TCommand, TResult>(typeof(THandler)));
        services.AddScoped<IAtomicCommandHandler<TCommand, TResult>, THandler>();
        return services;
    }

    public static IServiceCollection AddAtomicPersistenceKernel(
        this IServiceCollection services,
        bool allowUnconvertedWrites = false)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AtomicPersistenceMode(allowUnconvertedWrites));
        services.AddScoped<AtomicAuditScope>();
        services.AddScoped<IAtomicExecutionState>(provider =>
            provider.GetRequiredService<AtomicAuditScope>());
        services.AddScoped<IAtomicInfrastructureWriteGate>(provider =>
            provider.GetRequiredService<AtomicAuditScope>());
        services.AddScoped<IAtomicAuditEventSink>(provider =>
            provider.GetRequiredService<AtomicAuditScope>());
        services.AddScoped<AtomicAuditSaveChangesInterceptor>();
        services.AddScoped<AtomicTransactionLifecycleInterceptor>();
        services.AddScoped<AtomicSetBasedCommandGuardInterceptor>();
        services.AddScoped<WorkspaceAuthorityOwnershipInterceptor>();
        services.AddScoped<WorkspaceAccessRevisionGuard>();
        services.AddScoped<WorkOrderResponsibilityAccessRevisionGuard>();
        services.AddScoped<MembershipAssignmentScopeValidator>();
        services.AddScoped<ILegalDocumentSourceVersionResolver, LegalDocumentSourceVersionResolver>();
        services.TryAddSingleton<IAtomicUnitOfWork, AtomicUnitOfWork>();
        return services;
    }

    public static DbContextOptionsBuilder UseAtomicPersistenceKernel(
        this DbContextOptionsBuilder options,
        IServiceProvider services) =>
        options.AddInterceptors(
            services.GetRequiredService<AtomicAuditSaveChangesInterceptor>(),
            services.GetRequiredService<AtomicTransactionLifecycleInterceptor>(),
            services.GetRequiredService<AtomicSetBasedCommandGuardInterceptor>(),
            services.GetRequiredService<WorkspaceAuthorityOwnershipInterceptor>());

    public static DbContextOptionsBuilder<TContext> UseAtomicPersistenceKernel<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        IServiceProvider services)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)options).UseAtomicPersistenceKernel(services);
        return options;
    }
}

internal sealed record AtomicPersistenceMode(bool AllowUnconvertedWrites);
