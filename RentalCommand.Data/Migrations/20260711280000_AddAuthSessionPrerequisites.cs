using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Authorization;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711280000_AddAuthSessionPrerequisites")]
public sealed class AddAuthSessionPrerequisites : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_AuthSessionRefreshCredentials_ConsumedFacts",
            table: "AuthSessionRefreshCredentials");

        migrationBuilder.AddColumn<Guid>(
            name: "ConsumedByOperationId",
            table: "AuthSessionRefreshCredentials",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_AuthSessionRefreshCredentials_ConsumedFacts",
            table: "AuthSessionRefreshCredentials",
            sql: "(\"ConsumedAtUtc\" IS NULL AND \"ConsumedByOperationId\" IS NULL) OR " +
                 "(\"ConsumedAtUtc\" IS NOT NULL AND \"ConsumedByOperationId\" IS NOT NULL AND " +
                 "\"ConsumedAtUtc\" >= \"IssuedAtUtc\")");

        migrationBuilder.CreateTable(
            name: "LoginContextSelectionChallenges",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<int>(type: "integer", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoginContextSelectionChallenges", x => x.Id);
                table.CheckConstraint(
                    "CK_LoginContextSelectionChallenges_ConsumedFacts",
                    "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"CreatedAtUtc\"");
                table.CheckConstraint(
                    "CK_LoginContextSelectionChallenges_Expiry",
                    "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.ForeignKey(
                    name: "FK_LoginContextSelectionChallenges_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LoginContextSelectionChallenges_TokenHash",
            table: "LoginContextSelectionChallenges",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LoginContextSelectionChallenges_UserId_ExpiresAtUtc",
            table: "LoginContextSelectionChallenges",
            columns: new[] { "UserId", "ExpiresAtUtc" });

        // SECURITY: the API role remains subject to every base-table RLS policy while querying
        // this view. Keep the migration's security boundary explicit rather than relying on the
        // PostgreSQL default (security-definer view behavior).
        migrationBuilder.Sql(
            "CREATE VIEW \"vw_access_envelopes\" WITH (security_invoker = true) AS\n" +
            AccessEnvelopeViewSql.Definition);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(AccessEnvelopeViewSql.Drop);

        migrationBuilder.DropTable(name: "LoginContextSelectionChallenges");

        migrationBuilder.DropCheckConstraint(
            name: "CK_AuthSessionRefreshCredentials_ConsumedFacts",
            table: "AuthSessionRefreshCredentials");

        migrationBuilder.DropColumn(
            name: "ConsumedByOperationId",
            table: "AuthSessionRefreshCredentials");

        migrationBuilder.AddCheckConstraint(
            name: "CK_AuthSessionRefreshCredentials_ConsumedFacts",
            table: "AuthSessionRefreshCredentials",
            sql: "\"ConsumedAtUtc\" IS NULL OR \"ConsumedAtUtc\" >= \"IssuedAtUtc\"");
    }
}
