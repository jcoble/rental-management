using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankingReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountMask = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AccountType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AccountSubtype = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalItemIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExternalAccountIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankConnections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    BankConnectionId = table.Column<int>(type: "integer", nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AuthorizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MerchantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsoCurrencyCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MatchedPaymentId = table.Column<int>(type: "integer", nullable: true),
                    MatchedExpenseId = table.Column<int>(type: "integer", nullable: true),
                    MatchStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MatchConfidence = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RawData = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankTransactions_BankConnections_BankConnectionId",
                        column: x => x.BankConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Expenses_MatchedExpenseId",
                        column: x => x.MatchedExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Payments_MatchedPaymentId",
                        column: x => x.MatchedPaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_PortfolioId",
                table: "BankConnections",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_Status",
                table: "BankConnections",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_BankConnectionId",
                table: "BankTransactions",
                column: "BankConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_BankConnectionId_ProviderTransactionId",
                table: "BankTransactions",
                columns: new[] { "BankConnectionId", "ProviderTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedExpenseId",
                table: "BankTransactions",
                column: "MatchedExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedPaymentId",
                table: "BankTransactions",
                column: "MatchedPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchStatus",
                table: "BankTransactions",
                column: "MatchStatus");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId",
                table: "BankTransactions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PostedAt",
                table: "BankTransactions",
                column: "PostedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankTransactions");

            migrationBuilder.DropTable(
                name: "BankConnections");
        }
    }
}
