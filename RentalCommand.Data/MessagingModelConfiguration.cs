using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class MessagingModelConfiguration
{
    internal static void ConfigureConversations(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.LastMessagePreview).HasMaxLength(280);
            entity.HasIndex(e => new { e.PortfolioId, e.TenantId });
            entity.HasIndex(e => new { e.PortfolioId, e.WorkOrderId })
                .IsUnique()
                .HasFilter("\"WorkOrderId\" IS NOT NULL");
            entity.HasIndex(e => e.LastMessageAt);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            // Tenant is required (a conversation is always with one tenant); no cascade so deleting a
            // tenant does not silently destroy the thread history. Soft-delete is the norm anyway.
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.WorkOrder)
                .WithMany()
                .HasForeignKey(e => e.WorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(8000);
            entity.Property(e => e.Channels).HasMaxLength(100);
            // Stored as the string enum name to match the app-wide convention.
            entity.Property(e => e.SenderRole).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(e => e.ConversationId);
            // Cascade: deleting a conversation removes its messages.
            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}

