using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data.Authorization;

public static class AccessAuthorityDbFunctions
{
    public static bool IsEffective(int accessContextId, int userId, DateTime effectiveAtUtc) =>
        throw new InvalidOperationException("Access authority functions may only execute in PostgreSQL.");

    internal static void Configure(ModelBuilder modelBuilder)
    {
        var method = typeof(AccessAuthorityDbFunctions).GetMethod(
            nameof(IsEffective),
            [typeof(int), typeof(int), typeof(DateTime)])
            ?? throw new InvalidOperationException($"Could not resolve {nameof(IsEffective)}.");

        modelBuilder.HasDbFunction(method)
            .HasName("rc_access_context_is_effective");
    }
}
