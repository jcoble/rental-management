using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Data;

public class LifecycleDbContext : DbContext
{
    public LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : base(options) { }

    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Owner> Owners => Set<Owner>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<PortalMessage> PortalMessages => Set<PortalMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Portfolio>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ManagementCompanyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TimeZone).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Settings).HasMaxLength(10000);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<Owner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.MailingAddress).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Owners)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Property>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AddressLine1).IsRequired().HasMaxLength(250);
            entity.Property(e => e.AddressLine2).HasMaxLength(250);
            entity.Property(e => e.City).IsRequired().HasMaxLength(100);
            entity.Property(e => e.State).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PostalCode).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ManagementFeePercent).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Properties)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Owner)
                .WithMany(o => o.Properties)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Unit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UnitNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FloorPlan).HasMaxLength(100);
            entity.Property(e => e.Bedrooms).HasPrecision(4, 1);
            entity.Property(e => e.Bathrooms).HasPrecision(4, 1);
            entity.Property(e => e.MarketRent).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Units)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.EmergencyContact).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Tenants)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Lease>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LeaseNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.MonthlyRent).HasPrecision(18, 2);
            entity.Property(e => e.SecurityDeposit).HasPrecision(18, 2);
            entity.Property(e => e.LateFeeAmount).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Leases)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Leases)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Leases)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Leases)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Method).HasMaxLength(100);
            entity.Property(e => e.ExternalReference).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Payments)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Payments)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.WorkOrderId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Expenses)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.Expenses)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.WorkOrder)
                .WithMany(w => w.Expenses)
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ServiceType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.TaxId).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Vendors)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Category).HasMaxLength(120);
            entity.Property(e => e.EstimatedCost).HasPrecision(18, 2);
            entity.Property(e => e.ActualCost).HasPrecision(18, 2);
            entity.Property(e => e.CreatedBy).HasMaxLength(120);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.VendorId);
            entity.HasIndex(e => e.Priority);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.WorkOrders)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.WorkOrders)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.WorkOrders)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.WorkOrders)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Vendor)
                .WithMany(v => v.WorkOrders)
                .HasForeignKey(e => e.VendorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProspectName).HasMaxLength(200);
            entity.Property(e => e.ProspectEmail).HasMaxLength(200);
            entity.Property(e => e.AssignedTo).HasMaxLength(120);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Appointments)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Appointments)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Appointments)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Appointments)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Inspection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Outcome).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => e.LeaseId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledFor);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany(p => p.Inspections)
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.Inspections)
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Lease)
                .WithMany(l => l.Inspections)
                .HasForeignKey(e => e.LeaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).HasMaxLength(50);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Actor).HasMaxLength(120);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Type);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.Activities)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => new { e.PortfolioId, e.Email }).IsUnique();
            entity.HasIndex(e => e.Role);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.UserAccounts)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Owner)
                .WithMany(o => o.UserAccounts)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.UserAccounts)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasOne(e => e.UserAccount)
                .WithMany(u => u.Sessions)
                .HasForeignKey(e => e.UserAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PortalMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(5000);
            entity.Property(e => e.Reply).HasMaxLength(5000);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.UserAccountId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.PortalMessages)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.UserAccount)
                .WithMany(u => u.Messages)
                .HasForeignKey(e => e.UserAccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
