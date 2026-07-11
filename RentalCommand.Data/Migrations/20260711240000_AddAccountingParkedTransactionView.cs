using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711240000_AddAccountingParkedTransactionView")]
public sealed class AddAccountingParkedTransactionView : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_AccountingSyncMaps_ParkedPromotion",
            table: "AccountingSyncMaps",
            columns: new[] { "PortfolioId", "AccountingConnectionId", "ExternalType", "Id" },
            filter: "\"LocalEntityId\" IS NULL AND \"Direction\" = 'Import' AND \"Status\" IN ('NeedsReview', 'Unmatched')");

        migrationBuilder.Sql("""
            CREATE VIEW vw_accounting_parked_transactions WITH (security_invoker = true) AS
            SELECT
                m."Id",
                m."PortfolioId",
                m."AccountingConnectionId",
                m."ExternalType",
                m."ExternalId",
                m."MetadataJson" ->> 'CustomerExternalId' AS "CustomerExternalId",
                m."MetadataJson" ->> 'VendorExternalId' AS "VendorExternalId",
                m."MetadataJson" ->> 'AccountExternalId' AS "AccountExternalId",
                m."MetadataJson" ->> 'ClassExternalId' AS "ClassExternalId",
                m."MetadataJson" ->> 'DepositAccountExternalId' AS "DepositAccountExternalId",
                COALESCE(NULLIF(m."MetadataJson" ->> 'Amount', '')::numeric, 0) AS "Amount",
                COALESCE(NULLIF(m."MetadataJson" ->> 'TxnDateUtc', '')::timestamptz, '-infinity'::timestamptz) AS "TxnDateUtc",
                m."MetadataJson" ->> 'PaymentMethod' AS "PaymentMethod",
                m."MetadataJson" ->> 'ReferenceNumber' AS "ReferenceNumber",
                m."MetadataJson" ->> 'SourceKind' AS "SourceKind"
            FROM "AccountingSyncMaps" AS m
            WHERE m."MetadataJson" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS vw_accounting_parked_transactions;");
        migrationBuilder.DropIndex(
            name: "IX_AccountingSyncMaps_ParkedPromotion",
            table: "AccountingSyncMaps");
    }
}
