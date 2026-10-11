using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public static class LeasingPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddLeasingPersistenceServices(this IServiceCollection services)
    {
        services.AddScoped<ILegalDocumentSourceVersionResolver, LegalDocumentSourceVersionResolver>();
        return services;
    }
}
