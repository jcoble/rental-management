using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthAndPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAccounts_Owners_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Owners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserAccounts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserAccounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortalMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    PropertyId = table.Column<int>(type: "INTEGER", nullable: true),
                    UnitId = table.Column<int>(type: "INTEGER", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Reply = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalMessages_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortalMessages_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PortalMessages_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PortalMessages_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_ExpiresAt",
                table: "AuthSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_TokenHash",
                table: "AuthSessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserAccountId",
                table: "AuthSessions",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_PortfolioId",
                table: "PortalMessages",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_PropertyId",
                table: "PortalMessages",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_Status",
                table: "PortalMessages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_UnitId",
                table: "PortalMessages",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_UserAccountId",
                table: "PortalMessages",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_OwnerId",
                table: "UserAccounts",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_PortfolioId_Email",
                table: "UserAccounts",
                columns: new[] { "PortfolioId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_Role",
                table: "UserAccounts",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_TenantId",
                table: "UserAccounts",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropTable(
                name: "PortalMessages");

            migrationBuilder.DropTable(
                name: "UserAccounts");
        }
    }
}
