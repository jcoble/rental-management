namespace RentalCommand.Data.Accounting;

/// <summary>PostgreSQL-only security and invariant enforcement for the ledger foundation.</summary>
internal static class AccountingLedgerPostgreSql
{
    internal const string ApplySql = """
        ALTER TABLE "LedgerAccounts" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LedgerAccounts" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LedgerAccounts";
        CREATE POLICY tenant_isolation ON "LedgerAccounts"
          USING (rc_api_scope_allows("PortfolioId"))
          WITH CHECK (rc_api_scope_allows("PortfolioId"));

        ALTER TABLE "JournalEntries" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "JournalEntries" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "JournalEntries";
        CREATE POLICY tenant_isolation ON "JournalEntries"
          USING (rc_api_scope_allows("PortfolioId"))
          WITH CHECK (rc_api_scope_allows("PortfolioId"));

        ALTER TABLE "JournalLines" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "JournalLines" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "JournalLines";
        CREATE POLICY tenant_isolation ON "JournalLines"
          USING (EXISTS (
            SELECT 1 FROM "JournalEntries" entry
            WHERE entry."Id" = "JournalEntryId"
              AND rc_api_scope_allows(entry."PortfolioId")))
          WITH CHECK (EXISTS (
            SELECT 1 FROM "JournalEntries" entry
            WHERE entry."Id" = "JournalEntryId"
              AND rc_api_scope_allows(entry."PortfolioId")));

        ALTER TABLE "RecurringTenantCharges" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "RecurringTenantCharges" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "RecurringTenantCharges";
        CREATE POLICY tenant_isolation ON "RecurringTenantCharges"
          USING (rc_api_scope_allows("PortfolioId"))
          WITH CHECK (rc_api_scope_allows("PortfolioId"));

        ALTER TABLE "AccountingConversionReconciliations" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "AccountingConversionReconciliations" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "AccountingConversionReconciliations";
        CREATE POLICY tenant_isolation ON "AccountingConversionReconciliations"
          USING (rc_api_scope_allows("PortfolioId"))
          WITH CHECK (rc_api_scope_allows("PortfolioId"));

        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "LedgerAccounts" TO rentalcommand_api;
        GRANT SELECT ON TABLE "LedgerAccounts" TO rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "LedgerAccounts_Id_seq" TO rentalcommand_api, rentalcommand_engine;

        GRANT SELECT, INSERT ON TABLE "JournalEntries" TO rentalcommand_api, rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "JournalEntries_Id_seq" TO rentalcommand_api, rentalcommand_engine;
        GRANT SELECT, INSERT ON TABLE "JournalLines" TO rentalcommand_api, rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "JournalLines_Id_seq" TO rentalcommand_api, rentalcommand_engine;
        REVOKE UPDATE, DELETE ON TABLE "JournalEntries", "JournalLines" FROM rentalcommand_api, rentalcommand_engine;

        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "RecurringTenantCharges" TO rentalcommand_api;
        GRANT SELECT, INSERT, UPDATE ON TABLE "RecurringTenantCharges" TO rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "RecurringTenantCharges_Id_seq" TO rentalcommand_api, rentalcommand_engine;

        GRANT SELECT, INSERT, UPDATE ON TABLE "AccountingConversionReconciliations" TO rentalcommand_api, rentalcommand_engine;
        GRANT USAGE, SELECT ON SEQUENCE "AccountingConversionReconciliations_Id_seq" TO rentalcommand_api, rentalcommand_engine;

        CREATE OR REPLACE FUNCTION rc_accounting_validate_journal_line_scope()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            entry_portfolio_id integer;
        BEGIN
            SELECT entry."PortfolioId"
              INTO entry_portfolio_id
              FROM public."JournalEntries" entry
             WHERE entry."Id" = NEW."JournalEntryId";
            IF entry_portfolio_id IS NULL THEN
                RAISE EXCEPTION 'Journal entry is unavailable.' USING ERRCODE = '23503';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM public."LedgerAccounts" account
                 WHERE account."Id" = NEW."LedgerAccountId"
                   AND account."PortfolioId" = entry_portfolio_id) THEN
                RAISE EXCEPTION 'Journal account is outside the journal portfolio.' USING ERRCODE = '23514';
            END IF;
            IF NEW."PropertyId" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM public."Properties" property
                 WHERE property."Id" = NEW."PropertyId"
                   AND property."PortfolioId" = entry_portfolio_id) THEN
                RAISE EXCEPTION 'Journal property is outside the journal portfolio.' USING ERRCODE = '23514';
            END IF;
            IF NEW."UnitId" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM public."Units" unit
                 WHERE unit."Id" = NEW."UnitId"
                   AND unit."PortfolioId" = entry_portfolio_id) THEN
                RAISE EXCEPTION 'Journal unit is outside the journal portfolio.' USING ERRCODE = '23514';
            END IF;
            IF NEW."TenantAccountId" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM public."TenantAccounts" tenant_account
                 WHERE tenant_account."Id" = NEW."TenantAccountId"
                   AND tenant_account."PortfolioId" = entry_portfolio_id) THEN
                RAISE EXCEPTION 'Journal tenant account is outside the journal portfolio.' USING ERRCODE = '23514';
            END IF;
            IF NEW."OwnerEntityId" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM public."OwnerEntities" owner_entity
                 WHERE owner_entity."Id" = NEW."OwnerEntityId"
                   AND owner_entity."PortfolioId" = entry_portfolio_id) THEN
                RAISE EXCEPTION 'Journal owner entity is outside the journal portfolio.' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_JournalLines_PortfolioScope"
        BEFORE INSERT OR UPDATE ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_validate_journal_line_scope();

        CREATE OR REPLACE FUNCTION rc_accounting_validate_journal_balance()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            journal_entry_id integer;
            expected_currency text;
            total_debit numeric;
            total_credit numeric;
        BEGIN
            IF TG_TABLE_NAME = 'JournalLines' AND TG_OP = 'DELETE' THEN
                journal_entry_id := OLD."JournalEntryId";
            ELSIF TG_TABLE_NAME = 'JournalLines' THEN
                journal_entry_id := NEW."JournalEntryId";
            ELSIF TG_OP = 'DELETE' THEN
                journal_entry_id := OLD."Id";
            ELSE
                journal_entry_id := NEW."Id";
            END IF;

            SELECT entry."Currency"
              INTO expected_currency
              FROM public."JournalEntries" entry
             WHERE entry."Id" = journal_entry_id;
            IF expected_currency IS NULL THEN
                RETURN NULL;
            END IF;

            SELECT COALESCE(SUM(line."DebitAmount"), 0),
                   COALESCE(SUM(line."CreditAmount"), 0)
              INTO total_debit, total_credit
              FROM public."JournalLines" line
             WHERE line."JournalEntryId" = journal_entry_id;
            IF total_debit = 0 OR total_debit <> total_credit THEN
                RAISE EXCEPTION 'Journal entry % is not balanced for currency %.',
                    journal_entry_id, expected_currency USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END;
        $function$;

        CREATE CONSTRAINT TRIGGER "TR_JournalEntries_Balanced"
        AFTER INSERT OR UPDATE ON "JournalEntries"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_validate_journal_balance();
        CREATE CONSTRAINT TRIGGER "TR_JournalLines_Balanced"
        AFTER INSERT OR UPDATE OR DELETE ON "JournalLines"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_validate_journal_balance();

        CREATE OR REPLACE FUNCTION rc_accounting_reject_journal_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            RAISE EXCEPTION 'Posted journal history is immutable.' USING ERRCODE = '55000';
        END;
        $function$;
        CREATE TRIGGER "TR_JournalEntries_Immutable"
        BEFORE UPDATE OR DELETE ON "JournalEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_reject_journal_mutation();
        CREATE TRIGGER "TR_JournalLines_Immutable"
        BEFORE UPDATE OR DELETE ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_reject_journal_mutation();

        CREATE OR REPLACE FUNCTION rc_accounting_protect_account_history()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF OLD."IsSystem" OR EXISTS (
                    SELECT 1 FROM public."JournalLines" line
                     WHERE line."LedgerAccountId" = OLD."Id") THEN
                    RAISE EXCEPTION 'An account with required history cannot be deleted.' USING ERRCODE = '55000';
                END IF;
                RETURN OLD;
            END IF;
            IF OLD."IsSystem" AND (
                NEW."AccountType" IS DISTINCT FROM OLD."AccountType" OR
                NEW."NormalBalance" IS DISTINCT FROM OLD."NormalBalance" OR
                NEW."SystemKey" IS DISTINCT FROM OLD."SystemKey") THEN
                RAISE EXCEPTION 'A required system account cannot be retyped or reassigned.' USING ERRCODE = '55000';
            END IF;
            IF (NEW."AccountType" IS DISTINCT FROM OLD."AccountType" OR
                NEW."NormalBalance" IS DISTINCT FROM OLD."NormalBalance") AND EXISTS (
                SELECT 1 FROM public."JournalLines" line
                 WHERE line."LedgerAccountId" = OLD."Id") THEN
                RAISE EXCEPTION 'An account with posted history cannot be retyped.' USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        $function$;
        CREATE TRIGGER "TR_LedgerAccounts_ProtectHistory"
        BEFORE UPDATE OR DELETE ON "LedgerAccounts"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_protect_account_history();
        """;

    internal const string DropSql = """
        DROP TRIGGER IF EXISTS "TR_LedgerAccounts_ProtectHistory" ON "LedgerAccounts";
        DROP TRIGGER IF EXISTS "TR_JournalEntries_Immutable" ON "JournalEntries";
        DROP TRIGGER IF EXISTS "TR_JournalLines_Immutable" ON "JournalLines";
        DROP TRIGGER IF EXISTS "TR_JournalLines_PortfolioScope" ON "JournalLines";
        DROP TRIGGER IF EXISTS "TR_JournalEntries_Balanced" ON "JournalEntries";
        DROP TRIGGER IF EXISTS "TR_JournalLines_Balanced" ON "JournalLines";
        DROP FUNCTION IF EXISTS rc_accounting_protect_account_history();
        DROP FUNCTION IF EXISTS rc_accounting_reject_journal_mutation();
        DROP FUNCTION IF EXISTS rc_accounting_validate_journal_balance();
        DROP FUNCTION IF EXISTS rc_accounting_validate_journal_line_scope();
        DROP POLICY IF EXISTS tenant_isolation ON "LedgerAccounts";
        DROP POLICY IF EXISTS tenant_isolation ON "JournalEntries";
        DROP POLICY IF EXISTS tenant_isolation ON "JournalLines";
        DROP POLICY IF EXISTS tenant_isolation ON "RecurringTenantCharges";
        DROP POLICY IF EXISTS tenant_isolation ON "AccountingConversionReconciliations";
        REVOKE ALL PRIVILEGES ON TABLE "LedgerAccounts", "JournalEntries", "JournalLines",
            "RecurringTenantCharges", "AccountingConversionReconciliations"
            FROM rentalcommand_api, rentalcommand_engine;
        REVOKE ALL PRIVILEGES ON SEQUENCE "LedgerAccounts_Id_seq", "JournalEntries_Id_seq",
            "JournalLines_Id_seq", "RecurringTenantCharges_Id_seq",
            "AccountingConversionReconciliations_Id_seq" FROM rentalcommand_api, rentalcommand_engine;
        """;

    // Kept separate from ApplySql because ApplySql is invoked by an already-committed migration.
    // New database rules must be introduced by a later migration without rewriting history.
    internal const string HardenSql = """
        CREATE OR REPLACE FUNCTION rc_accounting_validate_journal_entry_currency()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            portfolio_currency text;
        BEGIN
            SELECT portfolio."Currency"
              INTO portfolio_currency
              FROM public."Portfolios" portfolio
             WHERE portfolio."Id" = NEW."PortfolioId";
            IF portfolio_currency IS NULL
               OR NEW."Currency" IS DISTINCT FROM portfolio_currency THEN
                RAISE EXCEPTION 'Journal currency must match the portfolio currency.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $function$;

        DROP TRIGGER IF EXISTS "TR_JournalEntries_PortfolioCurrency" ON "JournalEntries";
        CREATE TRIGGER "TR_JournalEntries_PortfolioCurrency"
        BEFORE INSERT ON "JournalEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_validate_journal_entry_currency();

        CREATE OR REPLACE FUNCTION rc_accounting_reject_late_journal_line_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            parent_xmin xid8;
        BEGIN
            SELECT entry.xmin::text::xid8
              INTO parent_xmin
              FROM public."JournalEntries" entry
             WHERE entry."Id" = NEW."JournalEntryId";
            IF parent_xmin IS NULL THEN
                RAISE EXCEPTION 'Journal entry is unavailable.' USING ERRCODE = '23503';
            END IF;
            -- A journal row written through an EF savepoint can have a subtransaction xmin.
            -- Its transaction status stays in progress until the owning transaction commits.
            IF pg_xact_status(parent_xmin) IS DISTINCT FROM 'in progress' THEN
                RAISE EXCEPTION 'Journal lines must be inserted in the journal entry transaction.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END;
        $function$;

        DROP TRIGGER IF EXISTS "TR_JournalLines_NoLateInsert" ON "JournalLines";
        CREATE TRIGGER "TR_JournalLines_NoLateInsert"
        BEFORE INSERT ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_reject_late_journal_line_insert();

        CREATE OR REPLACE FUNCTION rc_accounting_validate_exact_reversal()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            IF NEW."ReversesJournalEntryId" IS NULL THEN
                RETURN NULL;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                  FROM public."JournalEntries" original
                 WHERE original."Id" = NEW."ReversesJournalEntryId"
                   AND original."PortfolioId" = NEW."PortfolioId"
                   AND original."Currency" = NEW."Currency") THEN
                RAISE EXCEPTION 'A reversal must use the original journal portfolio and currency.'
                    USING ERRCODE = '23514';
            END IF;

            IF EXISTS (
                (SELECT original_line."LedgerAccountId", original_line."DebitAmount", original_line."CreditAmount",
                        original_line."Memo", original_line."PropertyId", original_line."UnitId",
                        original_line."TenantAccountId", original_line."OwnerEntityId",
                        original_line."SourceLineType", original_line."SourceLineId"
                   FROM public."JournalLines" original_line
                  WHERE original_line."JournalEntryId" = NEW."ReversesJournalEntryId")
                EXCEPT ALL
                (SELECT reversal_line."LedgerAccountId", reversal_line."CreditAmount", reversal_line."DebitAmount",
                        reversal_line."Memo", reversal_line."PropertyId", reversal_line."UnitId",
                        reversal_line."TenantAccountId", reversal_line."OwnerEntityId",
                        reversal_line."SourceLineType", reversal_line."SourceLineId"
                   FROM public."JournalLines" reversal_line
                  WHERE reversal_line."JournalEntryId" = NEW."Id")
            ) OR EXISTS (
                (SELECT reversal_line."LedgerAccountId", reversal_line."DebitAmount", reversal_line."CreditAmount",
                        reversal_line."Memo", reversal_line."PropertyId", reversal_line."UnitId",
                        reversal_line."TenantAccountId", reversal_line."OwnerEntityId",
                        reversal_line."SourceLineType", reversal_line."SourceLineId"
                   FROM public."JournalLines" reversal_line
                  WHERE reversal_line."JournalEntryId" = NEW."Id")
                EXCEPT ALL
                (SELECT original_line."LedgerAccountId", original_line."CreditAmount", original_line."DebitAmount",
                        original_line."Memo", original_line."PropertyId", original_line."UnitId",
                        original_line."TenantAccountId", original_line."OwnerEntityId",
                        original_line."SourceLineType", original_line."SourceLineId"
                   FROM public."JournalLines" original_line
                  WHERE original_line."JournalEntryId" = NEW."ReversesJournalEntryId")
            ) THEN
                RAISE EXCEPTION 'A reversal must exactly mirror the original journal entry.'
                    USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END;
        $function$;

        DROP TRIGGER IF EXISTS "TR_JournalEntries_ExactReversal" ON "JournalEntries";
        CREATE CONSTRAINT TRIGGER "TR_JournalEntries_ExactReversal"
        AFTER INSERT ON "JournalEntries"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_accounting_validate_exact_reversal();
        """;

    internal const string HardenDropSql = """
        DROP TRIGGER IF EXISTS "TR_JournalEntries_PortfolioCurrency" ON "JournalEntries";
        DROP TRIGGER IF EXISTS "TR_JournalLines_NoLateInsert" ON "JournalLines";
        DROP TRIGGER IF EXISTS "TR_JournalEntries_ExactReversal" ON "JournalEntries";
        DROP FUNCTION IF EXISTS rc_accounting_validate_journal_entry_currency();
        DROP FUNCTION IF EXISTS rc_accounting_reject_late_journal_line_insert();
        DROP FUNCTION IF EXISTS rc_accounting_validate_exact_reversal();
        """;
}
