using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Extensions;

/// <summary>
/// Registers the per-entity domain feature services (scoped). Each domain controller depends on one of
/// these; they own portfolio-scoped CRUD and realtime broadcasts. Later sub-units append their own
/// service registrations here so the wiring stays in one place. Call from <c>Program.cs</c> via
/// <c>builder.Services.AddDomainServices()</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        // --- controllers-inventory sub-unit ---
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IOwnerEntityService, OwnerEntityService>();
        services.AddScoped<IVendorService, VendorService>();

        // --- controllers-leasing-money sub-unit ---
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ILeaseService, LeaseService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<IAccountingService, AccountingService>();

        return services;
    }
}
