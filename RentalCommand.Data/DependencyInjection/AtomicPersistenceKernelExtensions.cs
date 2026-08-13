using System.Reflection;
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
    public static IServiceCollection AddAtomicCommandHandlersFrom(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var handlerDefinition = typeof(IAtomicCommandHandler<,>);
        var mappings = assemblies
            .Where(assembly => assembly is not null)
            .Distinct()
            .SelectMany(assembly => assembly.DefinedTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.ImplementedInterfaces
                .Where(candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == handlerDefinition)
                .Select(contract => new { Handler = type.AsType(), Contract = contract }))
            .OrderBy(mapping => mapping.Contract.FullName, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Handler.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var duplicate in mappings.GroupBy(mapping => mapping.Contract)
                     .Where(group => group.Select(mapping => mapping.Handler).Distinct().Count() > 1))
        {
            throw new InvalidOperationException(
                $"Multiple atomic handlers implement {duplicate.Key.FullName}: " +
                string.Join(", ", duplicate.Select(mapping => mapping.Handler.FullName)));
        }

        foreach (var mapping in mappings.DistinctBy(mapping => mapping.Contract))
        {
            if (services.Any(descriptor => descriptor.ServiceType == mapping.Contract))
            {
                throw new InvalidOperationException(
                    $"An atomic handler is already registered for {mapping.Contract.FullName}.");
            }

            services.AddScoped(mapping.Contract, mapping.Handler);
        }

        return services;
    }

    public static IServiceCollection AddAtomicCommandHandler<TCommand, TResult, THandler>(
        this IServiceCollection services)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
        where THandler : class, IAtomicCommandHandler<TCommand, TResult>
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IAtomicCommandHandler<TCommand, TResult>)))
        {
            throw new InvalidOperationException(
                $"An atomic handler is already registered for {typeof(TCommand).Name}/{typeof(TResult).Name}.");
        }

        services.AddScoped<IAtomicCommandHandler<TCommand, TResult>, THandler>();
        return services;
    }

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
        services.TryAddScoped<IAtomicUnitOfWork, AtomicUnitOfWork>();
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
