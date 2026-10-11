using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>Provider-neutral mappings for numeric operations that must remain inside SQL.</summary>
public static class SqlNumericFunctions
{
    public static decimal Round(decimal value, int decimalPlaces) =>
        throw new InvalidOperationException("SQL round may only be evaluated by a database provider.");

    internal static void Configure(ModelBuilder modelBuilder)
    {
        var method = typeof(SqlNumericFunctions).GetMethod(
            nameof(Round), [typeof(decimal), typeof(int)])
            ?? throw new InvalidOperationException($"Could not resolve {nameof(Round)}.");

        modelBuilder.HasDbFunction(method)
            .HasName("round")
            .IsBuiltIn();
    }
}
