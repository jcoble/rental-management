using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableProviderPaymentFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderFenceAcquiredAtUtc",
                table: "TenantPaymentAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderFenceToken",
                table: "TenantPaymentAttempts",
                type: "uuid",
                nullable: true);

            // Reinstall the canonical payment-attempt transition and open-account guards so an
            // upgraded database enforces the same durable fence as a freshly-created test schema.
            foreach (var statement in TenantAccountPostgreSqlContract.DropStatements)
                migrationBuilder.Sql(statement);
            foreach (var statement in TenantAccountPostgreSqlContract.CreateStatements)
                migrationBuilder.Sql(statement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderFenceAcquiredAtUtc",
                table: "TenantPaymentAttempts");

            migrationBuilder.DropColumn(
                name: "ProviderFenceToken",
                table: "TenantPaymentAttempts");
        }
    }
}
