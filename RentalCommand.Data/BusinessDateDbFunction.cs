using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>Simulation-aware portfolio business date, owned by PostgreSQL.</summary>
public static class BusinessDateDbFunction
{
    public const string Name = "rc_business_date";

    public static DateOnly ForPortfolio(int portfolioId) =>
        throw new InvalidOperationException($"{Name} may only be evaluated by the database provider.");

    internal static void Configure(ModelBuilder modelBuilder)
    {
        var method = typeof(BusinessDateDbFunction).GetMethod(
            nameof(ForPortfolio), [typeof(int)])
            ?? throw new InvalidOperationException($"Could not resolve {nameof(ForPortfolio)}.");

        modelBuilder.HasDbFunction(method).HasName(Name);
    }
}
