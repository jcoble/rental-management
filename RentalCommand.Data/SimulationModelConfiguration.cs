using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class SimulationModelConfiguration
{
    internal static void ConfigureSimulationInfrastructure(this ModelBuilder modelBuilder)
    {
        // Master Simulation Clock (dev/test only): one fixed row (Id = 1). Global — intentionally NOT
        // added to the tenant_isolation RLS policy set (see Migrations/*AddRls*), so a portfolio-scoped
        // session can still read it. Mode stored as a readable string (tiny table, low volume).
        modelBuilder.Entity<SimulationClock>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.TimeZoneId).HasMaxLength(64);
        });

        // Dev-only API→Engine command queue (dev/test only). Global — like SimulationClock, intentionally
        // NOT added to the tenant_isolation RLS policy set (see Migrations/*AddRls*), so the Engine's
        // admin session and the API's portfolio-scoped session can both read/write it. Guid PK is
        // app-assigned (ValueGeneratedNever). Status is a readable string; indexed for the claim query.
        modelBuilder.Entity<SimWorkerCommand>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.WorkerKey).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(16);
            entity.Property(e => e.ClaimOwner).HasMaxLength(200);
            entity.HasIndex(e => new { e.Status, e.ClaimExpiresAtUtc, e.CreatedRealUtc, e.Id });
        });

    }
}

