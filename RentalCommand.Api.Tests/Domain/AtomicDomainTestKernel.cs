using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Tests.Domain;

internal static class AtomicDomainTestKernel
{
    internal static ServiceProvider CreateForWorkOrders(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            CreateWorkOrderCommand,
            OperationMutationResult,
            CreateWorkOrderHandler>();
        services.AddAtomicCommandHandler<
            UpdateWorkOrderCommand,
            OperationMutationResult,
            UpdateWorkOrderHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForApplications(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            AtomicRentalMutationCommand,
            AtomicRentalMutationResult,
            AtomicRentalMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForCoreCrud(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            AtomicCoreCrudMutationCommand,
            AtomicCoreCrudMutationResult,
            AtomicCoreCrudMutationHandler>();
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Core(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(connectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider));
        return services;
    }

    private sealed class SqliteDatabaseClockInterceptor : DbConnectionInterceptor
    {
        internal static readonly SqliteDatabaseClockInterceptor Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Register(connection);
            base.ConnectionOpened(connection, eventData);
        }

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Register(connection);
            return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }

        private static void Register(DbConnection connection)
        {
            if (connection is SqliteConnection sqlite)
            {
                sqlite.CreateFunction("clock_timestamp", () => DateTime.UtcNow);
            }
        }
    }
}
