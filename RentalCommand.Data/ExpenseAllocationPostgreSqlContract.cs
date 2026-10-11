namespace RentalCommand.Data;

/// <summary>
/// PostgreSQL-owned Expense scope and allocation invariants installed by the post-baseline L02
/// migration. Deferred checks observe the final transaction state and serialize by Expense id.
/// </summary>
internal static class ExpenseAllocationPostgreSqlContract
{
    internal static IReadOnlyList<string> CreateStatements { get; } =
    [
        CreateScopeValidator,
        CreateBalanceValidator,
        CreateTriggers,
        CreatePrivilegesAndRls,
    ];

    internal static IReadOnlyList<string> DropStatements { get; } =
    [
        DropPrivilegesAndRls,
        DropTriggers,
        DropFunctions,
    ];

    private const string CreateScopeValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_expense_operational_scope()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          inherited_property_id integer;
          inherited_unit_id integer;
        BEGIN
          PERFORM pg_advisory_xact_lock(74002, NEW."Id");

          IF NEW."OperationalScope" = 'Portfolio' THEN
            IF NEW."PropertyId" IS NOT NULL OR NEW."UnitId" IS NOT NULL OR NEW."WorkOrderId" IS NOT NULL THEN
              RAISE EXCEPTION 'Portfolio Expense % cannot carry Property, Unit, or WorkOrder context', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          ELSIF NEW."OperationalScope" = 'Property' THEN
            IF NEW."PropertyId" IS NULL OR NEW."UnitId" IS NOT NULL OR NEW."WorkOrderId" IS NOT NULL
               OR NOT EXISTS (
                 SELECT 1 FROM "Properties" property
                 WHERE property."Id" = NEW."PropertyId"
                   AND property."PortfolioId" = NEW."PortfolioId") THEN
              RAISE EXCEPTION 'Property Expense % has invalid same-portfolio context', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          ELSIF NEW."OperationalScope" = 'Unit' THEN
            SELECT unit."PropertyId"
              INTO inherited_property_id
            FROM "Units" unit
            WHERE unit."Id" = NEW."UnitId"
              AND unit."PortfolioId" = NEW."PortfolioId";

            IF NEW."WorkOrderId" IS NOT NULL
               OR inherited_property_id IS NULL
               OR NEW."PropertyId" IS DISTINCT FROM inherited_property_id THEN
              RAISE EXCEPTION 'Unit Expense % does not persist its canonical same-portfolio Property', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          ELSIF NEW."OperationalScope" = 'WorkOrder' THEN
            SELECT work_order."PropertyId", work_order."UnitId"
              INTO inherited_property_id, inherited_unit_id
            FROM "WorkOrders" work_order
            WHERE work_order."Id" = NEW."WorkOrderId"
              AND work_order."PortfolioId" = NEW."PortfolioId";

            IF inherited_property_id IS NULL
               OR NEW."PropertyId" IS DISTINCT FROM inherited_property_id
               OR NEW."UnitId" IS DISTINCT FROM inherited_unit_id THEN
              RAISE EXCEPTION 'WorkOrder Expense % does not persist its canonical same-portfolio context', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          ELSE
            RAISE EXCEPTION 'Expense % has unsupported operational scope %',
              NEW."Id", NEW."OperationalScope"
              USING ERRCODE = '23514';
          END IF;

          RETURN NEW;
        END;
        $function$;
        """;

    private const string CreateBalanceValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_expense_allocation_balance()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          target_index integer;
          target_expense_ids integer[];
          target_portfolio_ids integer[];
          target_expense_id integer;
          target_portfolio_id integer;
          expense_amount numeric(18,2);
          allocation_count integer;
          allocation_total numeric(18,2);
        BEGIN
          IF TG_TABLE_NAME = 'Expenses' THEN
            target_expense_ids := ARRAY[NEW."Id"];
            target_portfolio_ids := ARRAY[NEW."PortfolioId"];
          ELSIF TG_OP = 'INSERT' THEN
            target_expense_ids := ARRAY[NEW."ExpenseId"];
            target_portfolio_ids := ARRAY[NEW."PortfolioId"];
          ELSIF TG_OP = 'DELETE' THEN
            target_expense_ids := ARRAY[OLD."ExpenseId"];
            target_portfolio_ids := ARRAY[OLD."PortfolioId"];
          ELSIF (OLD."ExpenseId", OLD."PortfolioId") =
                (NEW."ExpenseId", NEW."PortfolioId") THEN
            target_expense_ids := ARRAY[NEW."ExpenseId"];
            target_portfolio_ids := ARRAY[NEW."PortfolioId"];
          ELSIF (OLD."ExpenseId", OLD."PortfolioId") <
                (NEW."ExpenseId", NEW."PortfolioId") THEN
            target_expense_ids := ARRAY[OLD."ExpenseId", NEW."ExpenseId"];
            target_portfolio_ids := ARRAY[OLD."PortfolioId", NEW."PortfolioId"];
          ELSE
            target_expense_ids := ARRAY[NEW."ExpenseId", OLD."ExpenseId"];
            target_portfolio_ids := ARRAY[NEW."PortfolioId", OLD."PortfolioId"];
          END IF;

          FOR target_index IN 1..array_length(target_expense_ids, 1) LOOP
            target_expense_id := target_expense_ids[target_index];
            PERFORM pg_advisory_xact_lock(74002, target_expense_id);
          END LOOP;

          FOR target_index IN 1..array_length(target_expense_ids, 1) LOOP
            target_expense_id := target_expense_ids[target_index];
            target_portfolio_id := target_portfolio_ids[target_index];

            SELECT expense."Amount"
              INTO expense_amount
            FROM "Expenses" expense
            WHERE expense."Id" = target_expense_id
              AND expense."PortfolioId" = target_portfolio_id;

            IF expense_amount IS NOT NULL THEN
              SELECT COUNT(*), COALESCE(SUM(allocation."Amount"), 0)
                INTO allocation_count, allocation_total
              FROM "ExpenseAllocations" allocation
              WHERE allocation."ExpenseId" = target_expense_id
                AND allocation."PortfolioId" = target_portfolio_id;

              IF allocation_count > 0 AND allocation_total IS DISTINCT FROM expense_amount THEN
                RAISE EXCEPTION 'Expense % allocation total % must exactly equal amount %',
                  target_expense_id, allocation_total, expense_amount
                  USING ERRCODE = '23514';
              END IF;
            END IF;
          END LOOP;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateTriggers = """
        CREATE TRIGGER trg_expense_operational_scope
        BEFORE INSERT OR UPDATE OF "OperationalScope", "PortfolioId", "PropertyId", "UnitId", "WorkOrderId"
        ON "Expenses"
        FOR EACH ROW EXECUTE FUNCTION rc_validate_expense_operational_scope();

        CREATE CONSTRAINT TRIGGER trg_expense_allocation_balance_from_expense
        AFTER INSERT OR UPDATE OF "Amount" ON "Expenses"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_expense_allocation_balance();

        CREATE CONSTRAINT TRIGGER trg_expense_allocation_balance_from_allocation
        AFTER INSERT OR UPDATE OR DELETE ON "ExpenseAllocations"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_expense_allocation_balance();
        """;

    private const string CreatePrivilegesAndRls = """
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "ExpenseAllocations" TO rentalcommand_api;
        GRANT SELECT, INSERT, UPDATE ON TABLE "ExpenseAllocations" TO rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "ExpenseAllocations_Id_seq" TO rentalcommand_api;
        GRANT USAGE, SELECT ON SEQUENCE "ExpenseAllocations_Id_seq" TO rentalcommand_engine;

        ALTER TABLE "ExpenseAllocations" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "ExpenseAllocations" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_select ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_insert ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_update ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_delete ON "ExpenseAllocations";
        CREATE POLICY tenant_select ON "ExpenseAllocations" FOR SELECT
          USING (rc_api_scope_allows("PortfolioId"));
        CREATE POLICY tenant_insert ON "ExpenseAllocations" FOR INSERT
          WITH CHECK (rc_api_scope_allows("PortfolioId"));
        CREATE POLICY tenant_update ON "ExpenseAllocations" FOR UPDATE
          USING (rc_api_scope_allows("PortfolioId"))
          WITH CHECK (rc_api_scope_allows("PortfolioId"));
        CREATE POLICY tenant_delete ON "ExpenseAllocations" FOR DELETE
          USING (
            rc_api_scope_allows("PortfolioId")
            OR rc_sandbox_graduation_allows("PortfolioId"));
        """;

    private const string DropPrivilegesAndRls = """
        DROP POLICY IF EXISTS tenant_delete ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_update ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_insert ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_select ON "ExpenseAllocations";
        DROP POLICY IF EXISTS tenant_isolation ON "ExpenseAllocations";
        ALTER TABLE "ExpenseAllocations" NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE "ExpenseAllocations" DISABLE ROW LEVEL SECURITY;
        REVOKE USAGE, SELECT ON SEQUENCE "ExpenseAllocations_Id_seq" FROM rentalcommand_engine;
        REVOKE USAGE, SELECT ON SEQUENCE "ExpenseAllocations_Id_seq" FROM rentalcommand_api;
        REVOKE SELECT, INSERT, UPDATE ON TABLE "ExpenseAllocations" FROM rentalcommand_engine;
        REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLE "ExpenseAllocations" FROM rentalcommand_api;
        """;

    private const string DropTriggers = """
        DROP TRIGGER IF EXISTS trg_expense_allocation_balance_from_allocation ON "ExpenseAllocations";
        DROP TRIGGER IF EXISTS trg_expense_allocation_balance_from_expense ON "Expenses";
        DROP TRIGGER IF EXISTS trg_expense_operational_scope ON "Expenses";
        """;

    private const string DropFunctions = """
        DROP FUNCTION IF EXISTS rc_validate_expense_allocation_balance();
        DROP FUNCTION IF EXISTS rc_validate_expense_operational_scope();
        """;
}
