using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations;

public partial class AddExpenseOperationalScopeAndAllocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OperationalScope",
            table: "Expenses",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Portfolio");

        // Persist canonical inherited context before adding the exact shape constraint.
        migrationBuilder.Sql("""
            UPDATE "Expenses" expense
               SET "PropertyId" = work_order."PropertyId",
                   "UnitId" = work_order."UnitId",
                   "OperationalScope" = 'WorkOrder'
              FROM "WorkOrders" work_order
             WHERE expense."WorkOrderId" = work_order."Id"
               AND expense."PortfolioId" = work_order."PortfolioId";

            UPDATE "Expenses" expense
               SET "PropertyId" = unit."PropertyId",
                   "OperationalScope" = 'Unit'
              FROM "Units" unit
             WHERE expense."WorkOrderId" IS NULL
               AND expense."UnitId" = unit."Id"
               AND expense."PortfolioId" = unit."PortfolioId";

            UPDATE "Expenses"
               SET "OperationalScope" = 'Property'
             WHERE "WorkOrderId" IS NULL
               AND "UnitId" IS NULL
               AND "PropertyId" IS NOT NULL;
            """);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Expenses_Id_PortfolioId",
            table: "Expenses",
            columns: new[] { "Id", "PortfolioId" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Expenses_OperationalScope",
            table: "Expenses",
            sql: """
                ("OperationalScope" = 'Portfolio' AND "PropertyId" IS NULL AND "UnitId" IS NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'Property' AND "PropertyId" IS NOT NULL AND "UnitId" IS NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'Unit' AND "PropertyId" IS NOT NULL AND "UnitId" IS NOT NULL AND "WorkOrderId" IS NULL)
                OR ("OperationalScope" = 'WorkOrder' AND "PropertyId" IS NOT NULL AND "WorkOrderId" IS NOT NULL)
                """);

        migrationBuilder.CreateTable(
            name: "ExpenseAllocations",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                ExpenseId = table.Column<int>(type: "integer", nullable: false),
                TargetKind = table.Column<string>(
                    type: "character varying(20)", maxLength: 20, nullable: false),
                PropertyId = table.Column<int>(type: "integer", nullable: true),
                UnitId = table.Column<int>(type: "integer", nullable: true),
                OwnerEntityId = table.Column<int>(type: "integer", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExpenseAllocations", x => x.Id);
                table.CheckConstraint("CK_ExpenseAllocations_PositiveAmount", "\"Amount\" > 0");
                table.CheckConstraint(
                    "CK_ExpenseAllocations_TypedTarget",
                    """
                    ("TargetKind" = 'Property' AND "PropertyId" IS NOT NULL AND "UnitId" IS NULL AND "OwnerEntityId" IS NULL)
                    OR ("TargetKind" = 'Unit' AND "PropertyId" IS NULL AND "UnitId" IS NOT NULL AND "OwnerEntityId" IS NULL)
                    OR ("TargetKind" = 'OwnerEntity' AND "PropertyId" IS NULL AND "UnitId" IS NULL AND "OwnerEntityId" IS NOT NULL)
                    """);
                table.ForeignKey(
                    name: "FK_ExpenseAllocations_Expenses_ExpenseId_PortfolioId",
                    columns: x => new { x.ExpenseId, x.PortfolioId },
                    principalTable: "Expenses",
                    principalColumns: new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ExpenseAllocations_OwnerEntities_OwnerEntityId_PortfolioId",
                    columns: x => new { x.OwnerEntityId, x.PortfolioId },
                    principalTable: "OwnerEntities",
                    principalColumns: new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ExpenseAllocations_Properties_PropertyId_PortfolioId",
                    columns: x => new { x.PropertyId, x.PortfolioId },
                    principalTable: "Properties",
                    principalColumns: new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ExpenseAllocations_Units_UnitId_PortfolioId",
                    columns: x => new { x.UnitId, x.PortfolioId },
                    principalTable: "Units",
                    principalColumns: new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_ExpenseId_PortfolioId",
            table: "ExpenseAllocations",
            columns: new[] { "ExpenseId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_OwnerEntityId_PortfolioId",
            table: "ExpenseAllocations",
            columns: new[] { "OwnerEntityId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_PortfolioId_ExpenseId",
            table: "ExpenseAllocations",
            columns: new[] { "PortfolioId", "ExpenseId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_PortfolioId_OwnerEntityId",
            table: "ExpenseAllocations",
            columns: new[] { "PortfolioId", "OwnerEntityId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_PortfolioId_PropertyId",
            table: "ExpenseAllocations",
            columns: new[] { "PortfolioId", "PropertyId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_PortfolioId_UnitId",
            table: "ExpenseAllocations",
            columns: new[] { "PortfolioId", "UnitId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_PropertyId_PortfolioId",
            table: "ExpenseAllocations",
            columns: new[] { "PropertyId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            name: "IX_ExpenseAllocations_UnitId_PortfolioId",
            table: "ExpenseAllocations",
            columns: new[] { "UnitId", "PortfolioId" });

        foreach (var statement in ExpenseAllocationPostgreSqlContract.CreateStatements)
            migrationBuilder.Sql(statement);

        // Run the trigger validator across every backfilled row after all inherited context is set.
        migrationBuilder.Sql("""
            UPDATE "Expenses"
               SET "OperationalScope" = "OperationalScope";
            SET CONSTRAINTS
              trg_expense_allocation_balance_from_expense,
              trg_expense_allocation_balance_from_allocation
              IMMEDIATE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in ExpenseAllocationPostgreSqlContract.DropStatements)
            migrationBuilder.Sql(statement);

        migrationBuilder.DropTable(name: "ExpenseAllocations");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Expenses_OperationalScope",
            table: "Expenses");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_Expenses_Id_PortfolioId",
            table: "Expenses");
        migrationBuilder.DropColumn(
            name: "OperationalScope",
            table: "Expenses");
    }
}
