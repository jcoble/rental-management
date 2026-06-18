using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingIntegrationBackbone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccessTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RefreshTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PullEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PushEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastPulledAtJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisconnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingConnections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    StateToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CodeVerifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthStates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountingEntityMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                    LocalEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LocalEntityId = table.Column<int>(type: "integer", nullable: true),
                    LocalEnumValue = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingEntityMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingEntityMappings_AccountingConnections_AccountingCo~",
                        column: x => x.AccountingConnectionId,
                        principalTable: "AccountingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingEntityMappings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountingSyncMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExternalType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LocalEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LocalEntityId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingSyncMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingSyncMaps_AccountingConnections_AccountingConnecti~",
                        column: x => x.AccountingConnectionId,
                        principalTable: "AccountingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountingSyncMaps_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_PortfolioId",
                table: "AccountingConnections",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_PortfolioId_Provider",
                table: "AccountingConnections",
                columns: new[] { "PortfolioId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_AccountingConnectionId",
                table: "AccountingEntityMappings",
                column: "AccountingConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_PortfolioId",
                table: "AccountingEntityMappings",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntityMappings_PortfolioId_AccountingConnectionId~",
                table: "AccountingEntityMappings",
                columns: new[] { "PortfolioId", "AccountingConnectionId", "ExternalType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_AccountingConnectionId",
                table: "AccountingSyncMaps",
                column: "AccountingConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_PortfolioId",
                table: "AccountingSyncMaps",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSyncMaps_PortfolioId_AccountingConnectionId_Direc~",
                table: "AccountingSyncMaps",
                columns: new[] { "PortfolioId", "AccountingConnectionId", "Direction", "ExternalType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_ExpiresAt",
                table: "OAuthStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_PortfolioId",
                table: "OAuthStates",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStates_StateToken",
                table: "OAuthStates",
                column: "StateToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingEntityMappings");

            migrationBuilder.DropTable(
                name: "AccountingSyncMaps");

            migrationBuilder.DropTable(
                name: "OAuthStates");

            migrationBuilder.DropTable(
                name: "AccountingConnections");
        }
    }
}
