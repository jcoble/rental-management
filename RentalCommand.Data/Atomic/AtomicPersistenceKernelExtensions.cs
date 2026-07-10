using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Deliberate opt-in registration for converted write paths. The existing application does not call
/// either extension, so its legacy audit/write behavior remains unchanged during migration.
/// </summary>
public static class AtomicPersistenceKernelExtensions
{
    public static IServiceCollection AddAtomicPersistenceKernel(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AtomicAuditScope>();
        services.AddScoped<AtomicAuditSaveChangesInterceptor>();
        services.AddScoped<AtomicTransactionLifecycleInterceptor>();
        services.AddScoped<AtomicSetBasedCommandGuardInterceptor>();
        services.AddScoped<IAtomicSetBasedMutationExecutor, AtomicSetBasedMutationExecutor>();
        services.TryAddSingleton<IAtomicUnitOfWork, AtomicUnitOfWork>();
        return services;
    }

    public static DbContextOptionsBuilder UseAtomicPersistenceKernel(
        this DbContextOptionsBuilder options,
        IServiceProvider services) =>
        options.AddInterceptors(
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
