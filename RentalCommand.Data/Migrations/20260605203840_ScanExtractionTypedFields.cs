using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanExtractionTypedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractedData",
                table: "WorkOrders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Payments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckNumber",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractedData",
                table: "Payments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerName",
                table: "Payments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractedData",
                table: "Leases",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CardLast4",
                table: "Expenses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentKind",
                table: "Expenses",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Expenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseLineItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseLineItems_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLineItems_ExpenseId",
                table: "ExpenseLineItems",
                column: "ExpenseId");

            // -----------------------------------------------------------------
            // Backfill existing scan-created Expenses from the JSON already stored in ReceiptData.
            // Both statements are guarded so they only touch rows whose JSON has the expected shape
            // (object / array), and numerics are cast only when the text form is a numeric literal so
            // blank/missing/garbage values become NULL instead of raising. Nothing is dropped:
            // ReceiptData stays the superset. Payment/Lease/WorkOrder ExtractedData is left NULL —
            // those tables had no extraction blob to copy from, so there is nothing to backfill.
            // -----------------------------------------------------------------

            // 1. Promote Expense scalar fields from the stored ReceiptData JSON object.
            migrationBuilder.Sql("""
                UPDATE "Expenses"
                SET "PaymentMethod" = NULLIF("ReceiptData"->>'paymentMethod',''),
                    "CardLast4"     = NULLIF("ReceiptData"->>'cardLast4',''),
                    "DocumentKind"  = NULLIF("ReceiptData"->>'documentKind','')
                WHERE "ReceiptData" IS NOT NULL
                  AND jsonb_typeof("ReceiptData") = 'object';
                """);

            // 2. Promote Expense line items (ReceiptData->'lineItems' array) into the new child table.
            //    WITH ORDINALITY supplies the 1-based LineNumber; the lineItems path is guarded to be a
            //    JSON array and each element to be a JSON object so malformed data is skipped, not thrown on.
            //    Numerics are cast only when the text form matches a numeric literal — a non-numeric string
            //    (e.g. a hand-edited "2 pcs") becomes NULL instead of aborting the migration with a cast error.
            //    Description is truncated to the column's 1000-char limit so an oversized line can't fail insert.
            migrationBuilder.Sql("""
                INSERT INTO "ExpenseLineItems"
                    ("ExpenseId", "Description", "Quantity", "UnitPrice", "Amount", "LineNumber")
                SELECT e."Id",
                       LEFT(COALESCE(NULLIF(li->>'description',''), ''), 1000),
                       CASE WHEN li->>'quantity'  ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'quantity')::numeric  END,
                       CASE WHEN li->>'unitPrice' ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'unitPrice')::numeric END,
                       CASE WHEN li->>'amount'    ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'amount')::numeric    END,
                       ord::int
                FROM "Expenses" e
                CROSS JOIN LATERAL
                    jsonb_array_elements(e."ReceiptData"->'lineItems') WITH ORDINALITY AS t(li, ord)
                WHERE e."ReceiptData" IS NOT NULL
                  AND jsonb_typeof(e."ReceiptData") = 'object'
                  AND jsonb_typeof(e."ReceiptData"->'lineItems') = 'array'
                  AND jsonb_typeof(li) = 'object';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseLineItems");

            migrationBuilder.DropColumn(
                name: "ExtractedData",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CheckNumber",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExtractedData",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayerName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExtractedData",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "CardLast4",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "DocumentKind",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Expenses");
        }
    }
}
