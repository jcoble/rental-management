using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Atomic;

/// <summary>Registers the atomic command kernel and its generic transaction/session infrastructure.</summary>
public static class AtomicPersistenceKernelExtensions
{
    public static IServiceCollection AddAtomicPersistenceKernel(
        this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AtomicAuditScope>();
        services.AddScoped<AtomicCommandContext>();
        services.AddScoped<IAtomicCommandContext>(provider =>
            provider.GetRequiredService<AtomicCommandContext>());
        services.AddScoped<AtomicAuditSaveChangesInterceptor>();
        services.AddScoped<WorkspaceAuthorityOwnershipInterceptor>();
        services.AddScoped<AtomicTransactionLifecycleInterceptor>();
        services.AddScoped<AtomicSetBasedCommandGuardInterceptor>();
        services.TryAddScoped<WorkspaceAccessRevisionGuard>();
        services.TryAddScoped<WorkOrderResponsibilityAccessRevisionGuard>();
        services.TryAddScoped<IMembershipAssignmentScopeValidator, MembershipAssignmentScopeValidator>();
        services.TryAddScoped<AtomicTransactionRunner>();
        services.TryAddScoped<IWriteExecutor, WriteExecutor>();
        return services;
    }

    public static DbContextOptionsBuilder UseAtomicPersistenceKernel(
        this DbContextOptionsBuilder options,
        IServiceProvider services) =>
        options.AddInterceptors(
            services.GetRequiredService<WorkspaceAuthorityOwnershipInterceptor>(),
            services.GetRequiredService<AtomicAuditSaveChangesInterceptor>(),
            services.GetRequiredService<AtomicTransactionLifecycleInterceptor>(),
            services.GetRequiredService<AtomicSetBasedCommandGuardInterceptor>());

    public static DbContextOptionsBuilder<TContext> UseAtomicPersistenceKernel<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        IServiceProvider services)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)options).UseAtomicPersistenceKernel(services);
        return options;
    }
}
