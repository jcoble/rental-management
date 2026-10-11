using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Screening;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Auth;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Scanning;
using RentalCommand.Data.Screening;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

internal static class AtomicDomainTestKernel
{
    internal static ServiceProvider CreateForWorkOrders(string connectionString)
    {
        var services = Core(connectionString);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAppointments(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = Core(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAppointmentsPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForWorkOrdersPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForVendorDispatchPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForApplications(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = Core(connectionString, interceptors: interceptors);
        services.AddScoped<RentalCommandDbContext>(provider =>
            new SqliteCompatibleRentalCommandDbContext(
                provider.GetRequiredService<DbContextOptions<RentalCommandDbContext>>()));
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForCoreCrud(string connectionString, TimeProvider? timeProvider = null)
    {
        var services = Core(connectionString, timeProvider);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForRecurringMaintenance(
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        var services = Core(connectionString, timeProvider);
        services.AddScoped<RecurringMaintenanceTaskService>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForCoreCrudPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForRentalCrudPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddSingleton(Mock.Of<IAuditTrailService>());
        services.AddScoped<UnitService>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForMoneyPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScheduledTenantChargesPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForInspectionsPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, interceptors: interceptors);
        services.AddPendingFileUploadStore();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScreeningPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddPendingFileUploadStore();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAccountBootstrapPostgreSql(NpgsqlConnection connection)
    {
        var services = CorePostgreSql(connection);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForPasswordResetPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScanRetryPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Core(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseSqlite(connectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider);
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
        });
        return services;
    }

    private static ServiceCollection CorePostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider);
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
        });
        return services;
    }

    private static ServiceCollection CorePostgreSql(NpgsqlConnection connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connection)
                .UseAtomicPersistenceKernel(provider));
        return services;
    }

}
