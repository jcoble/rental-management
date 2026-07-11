using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711250000_AddBoundedAccountingAndPlaidExchangeAdmission")]
public sealed class AddBoundedAccountingAndPlaidExchangeAdmission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "AccountingEntityMappings",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "AccountingMappingPromotionJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                AccountingConnectionId = table.Column<int>(type: "integer", nullable: false),
                AccountingEntityMappingId = table.Column<int>(type: "integer", nullable: false),
                MappingRevision = table.Column<long>(type: "bigint", nullable: false),
                PromotedCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountingMappingPromotionJobs", x => x.Id);
                table.ForeignKey("FK_AccountingMappingPromotionJobs_AccountingConnections_AccountingConnectionId", x => x.AccountingConnectionId, "AccountingConnections", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_AccountingMappingPromotionJobs_AccountingEntityMappings_AccountingEntityMappingId", x => x.AccountingEntityMappingId, "AccountingEntityMappings", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_AccountingMappingPromotionJobs_Portfolios_PortfolioId", x => x.PortfolioId, "Portfolios", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PlaidTokenExchangeAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                ClientOperationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PublicTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InstitutionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                AccountMask = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                AccountType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                AccountSubtype = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                ExternalAccountIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                ExternalAccountIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                RemoteAdmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RemoteReceiptRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ProviderRequestIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ExternalItemIdCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                ExternalItemIdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ExternalAccessTokenCipherText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                BankConnectionId = table.Column<int>(type: "integer", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlaidTokenExchangeAttempts", x => x.Id);
                table.ForeignKey("FK_PlaidTokenExchangeAttempts_BankConnections_BankConnectionId", x => x.BankConnectionId, "BankConnections", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_PlaidTokenExchangeAttempts_Portfolios_PortfolioId", x => x.PortfolioId, "Portfolios", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_AccountingMappingPromotionJobs_AccountingConnectionId", "AccountingMappingPromotionJobs", "AccountingConnectionId");
        migrationBuilder.CreateIndex("IX_AccountingMappingPromotionJobs_AccountingEntityMappingId_MappingRevision", "AccountingMappingPromotionJobs", new[] { "AccountingEntityMappingId", "MappingRevision" }, unique: true);
        migrationBuilder.CreateIndex("IX_AccountingMappingPromotionJobs_PortfolioId_CompletedAtUtc_Id", "AccountingMappingPromotionJobs", new[] { "PortfolioId", "CompletedAtUtc", "Id" });
        migrationBuilder.CreateIndex("IX_PlaidTokenExchangeAttempts_BankConnectionId", "PlaidTokenExchangeAttempts", "BankConnectionId");
        migrationBuilder.CreateIndex("IX_PlaidTokenExchangeAttempts_PortfolioId_ClientOperationId", "PlaidTokenExchangeAttempts", new[] { "PortfolioId", "ClientOperationId" }, unique: true);
        migrationBuilder.CreateIndex("IX_PlaidTokenExchangeAttempts_PortfolioId_Status_PreparedAtUtc", "PlaidTokenExchangeAttempts", new[] { "PortfolioId", "Status", "PreparedAtUtc" });

        migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"AccountingMappingPromotionJobs\", \"PlaidTokenExchangeAttempts\" TO rentalcommand_api;");

        foreach (var table in new[] { "AccountingMappingPromotionJobs", "PlaidTokenExchangeAttempts" })
        {
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"""
                CREATE POLICY tenant_isolation ON "{table}"
                USING ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
                    OR current_setting('app.is_admin', true) = 'true')
                WITH CHECK ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
                    OR current_setting('app.is_admin', true) = 'true');
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AccountingMappingPromotionJobs");
        migrationBuilder.DropTable(name: "PlaidTokenExchangeAttempts");
        migrationBuilder.DropColumn(name: "Revision", table: "AccountingEntityMappings");
    }
}
