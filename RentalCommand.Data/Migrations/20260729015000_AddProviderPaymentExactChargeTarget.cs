using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729015000_AddProviderPaymentExactChargeTarget")]
public sealed partial class AddProviderPaymentExactChargeTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ChargeLedgerEntryId",
            table: "TenantPaymentAttempts",
            type: "bigint",
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "AttemptType",
            table: "TenantPaymentAttempts",
            type: "character varying(30)",
            maxLength: 30,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(20)",
            oldMaxLength: 20);

        migrationBuilder.DropCheckConstraint(
            name: "CK_TenantPaymentAttempt_Type",
            table: "TenantPaymentAttempts");
        migrationBuilder.DropCheckConstraint(
            name: "CK_TenantPaymentAttempt_Amount",
            table: "TenantPaymentAttempts");

        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION rc_validate_tenant_payment_attempt_success()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            DECLARE
              attempt_id bigint;
              attempt record;
              linked_entry_count integer;
              matching_entry_count integer;
            BEGIN
              IF TG_TABLE_NAME = 'TenantPaymentAttempts' THEN
                attempt_id := NEW."Id";
              ELSE
                attempt_id := NEW."ProviderPaymentAttemptId";
                IF attempt_id IS NULL THEN
                  RETURN NULL;
                END IF;
              END IF;

              SELECT row."Id", row."PortfolioId", row."TenantAccountId", row."AttemptType",
                     row."State", row."Amount", row."Currency"
                INTO attempt
              FROM "TenantPaymentAttempts" AS row
              WHERE row."Id" = attempt_id;

              IF NOT FOUND THEN
                RETURN NULL;
              END IF;

              SELECT COUNT(*),
                     COUNT(*) FILTER (
                       WHERE entry."PortfolioId" = attempt."PortfolioId"
                         AND entry."TenantAccountId" = attempt."TenantAccountId"
                         AND entry."Amount" = attempt."Amount"
                         AND entry."Currency" = attempt."Currency"
                         AND ((attempt."AttemptType" IN (
                                  'Charge',
                                  'UnappliedReceipt',
                                  'DepositReceipt',
                                  'ImportedReceipt')
                               AND entry."EntryType" = 'PaymentReceipt'
                               AND entry."Direction" = 'Credit')
                           OR (attempt."AttemptType" = 'Refund'
                               AND entry."EntryType" = 'Refund'
                               AND entry."Direction" = 'Debit')))
                INTO linked_entry_count, matching_entry_count
              FROM "TenantLedgerEntries" AS entry
              WHERE entry."ProviderPaymentAttemptId" = attempt."Id";

              IF attempt."AttemptType" IN (
                   'Charge',
                   'UnappliedReceipt',
                   'DepositReceipt',
                   'ImportedReceipt',
                   'Refund')
                 AND attempt."State" = 'Succeeded' THEN
                IF linked_entry_count <> 1 OR matching_entry_count <> 1 THEN
                  RAISE EXCEPTION 'Succeeded TenantPaymentAttempt % requires exactly one matching ledger entry',
                    attempt."Id" USING ERRCODE = '23514';
                END IF;
              ELSIF attempt."AttemptType" = 'LegacyTargetlessCharge' THEN
                RETURN NULL;
              ELSIF linked_entry_count <> 0 THEN
                RAISE EXCEPTION 'TenantPaymentAttempt % ledger entry requires a succeeded receipt or Refund',
                  attempt."Id" USING ERRCODE = '23514';
              END IF;

              RETURN NULL;
            END;
            $function$;
            """);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
              IF TG_OP IS DISTINCT FROM 'UPDATE'
                 OR current_setting(
                      'rental_command.tenant_payment_attempt_exact_target_migration',
                      true) IS DISTINCT FROM 'on'
                 OR OLD."AttemptType" IS DISTINCT FROM 'Charge'
                 OR (to_jsonb(NEW) - ARRAY['AttemptType', 'ChargeLedgerEntryId'])
                    IS DISTINCT FROM
                    (to_jsonb(OLD) - ARRAY['AttemptType', 'ChargeLedgerEntryId'])
                 OR NOT (
                      (NEW."AttemptType" = 'Charge'
                       AND OLD."ChargeLedgerEntryId" IS NULL
                       AND NEW."ChargeLedgerEntryId" IS NOT NULL)
                   OR (NEW."AttemptType" = 'LegacyTargetlessCharge'
                       AND OLD."ChargeLedgerEntryId" IS NULL
                       AND NEW."ChargeLedgerEntryId" IS NULL)
                 ) THEN
                RAISE EXCEPTION 'TenantPaymentAttempt migration rewrite is not an exact supported shape'
                  USING ERRCODE = '23514';
              END IF;

              RETURN NEW;
            END;
            $function$;

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_restore_guard',
              CASE
                WHEN to_regprocedure('rc_guard_tenant_payment_attempt_write()') IS NOT NULL
                 AND EXISTS (
                      SELECT 1
                      FROM pg_trigger
                      WHERE tgrelid = '"TenantPaymentAttempts"'::regclass
                        AND tgname = 'trg_tenant_payment_attempt_write'
                        AND tgfoid =
                            to_regprocedure('rc_guard_tenant_payment_attempt_write()')
                        AND NOT tgisinternal)
                THEN 'on'
                ELSE 'off'
              END,
              true);

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write
              ON "TenantPaymentAttempts";
            CREATE TRIGGER trg_tenant_payment_attempt_write
              BEFORE INSERT OR UPDATE ON "TenantPaymentAttempts"
              FOR EACH ROW
              EXECUTE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration();

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_migration',
              'on',
              true);

            UPDATE "TenantPaymentAttempts" AS attempt
               SET "ChargeLedgerEntryId" = target."Id"
              FROM "TenantLedgerEntries" AS target
             WHERE attempt."AttemptType" = 'Charge'
               AND attempt."ChargeLedgerEntryId" IS NULL
               AND attempt."IdempotencyKey" ~
                   '^(intent|checkout|autopay):tenant-charge:[0-9]+$'
               AND target."Id"::text =
                   split_part(attempt."IdempotencyKey", ':', 3)
               AND target."PortfolioId" = attempt."PortfolioId"
               AND target."TenantAccountId" = attempt."TenantAccountId"
               AND target."Direction" = 'Debit';

            UPDATE "TenantPaymentAttempts" AS attempt
               SET "AttemptType" = 'LegacyTargetlessCharge'
             WHERE attempt."AttemptType" = 'Charge'
               AND attempt."ChargeLedgerEntryId" IS NULL;

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_migration',
              'off',
              true);

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write
              ON "TenantPaymentAttempts";

            DO $restore_guard$
            BEGIN
              IF current_setting(
                   'rental_command.tenant_payment_attempt_exact_target_restore_guard',
                   true) = 'on' THEN
                EXECUTE $trigger$
                  CREATE TRIGGER trg_tenant_payment_attempt_write
                    BEFORE INSERT OR UPDATE ON "TenantPaymentAttempts"
                    FOR EACH ROW
                    EXECUTE FUNCTION rc_guard_tenant_payment_attempt_write()
                $trigger$;
              END IF;
            END;
            $restore_guard$;

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_restore_guard',
              'off',
              true);

            DROP FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration();

            SET CONSTRAINTS ALL IMMEDIATE;
            SET CONSTRAINTS ALL DEFERRED;
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_TenantPaymentAttempt_Type",
            table: "TenantPaymentAttempts",
            sql: "\"AttemptType\" IN ('Charge', 'UnappliedReceipt', 'DepositReceipt', " +
                 "'ImportedReceipt', 'LegacyTargetlessCharge', 'Refund', 'Verification')");
        migrationBuilder.AddCheckConstraint(
            name: "CK_TenantPaymentAttempt_Amount",
            table: "TenantPaymentAttempts",
            sql: "(\"AttemptType\" = 'Verification' AND \"Amount\" = 0) OR " +
                 "(\"AttemptType\" <> 'Verification' AND \"Amount\" > 0)");
        migrationBuilder.AddCheckConstraint(
            name: "CK_TenantPaymentAttempt_ChargeTarget",
            table: "TenantPaymentAttempts",
            sql: "(\"AttemptType\" = 'Charge' AND \"ChargeLedgerEntryId\" IS NOT NULL) OR " +
                 "(\"AttemptType\" <> 'Charge' AND \"ChargeLedgerEntryId\" IS NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_TenantPaymentAttempts_ChargeLedgerEntryId_TenantAccountId_PortfolioId",
            table: "TenantPaymentAttempts",
            columns: new[] { "ChargeLedgerEntryId", "TenantAccountId", "PortfolioId" },
            filter: "\"ChargeLedgerEntryId\" IS NOT NULL");

        migrationBuilder.AddForeignKey(
            name: "FK_TenantPaymentAttempts_TenantLedgerEntries_ChargeLedgerEntryId_TenantAccountId_PortfolioId",
            table: "TenantPaymentAttempts",
            columns: new[] { "ChargeLedgerEntryId", "TenantAccountId", "PortfolioId" },
            principalTable: "TenantLedgerEntries",
            principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION rc_enforce_payment_attempt_intent()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
              IF TG_OP = 'INSERT' AND NEW."AttemptType" = 'LegacyTargetlessCharge' THEN
                RAISE EXCEPTION 'LegacyTargetlessCharge is retired historical data'
                  USING ERRCODE = '23514';
              END IF;
              IF TG_OP = 'UPDATE'
                 AND NEW."AttemptType" = 'LegacyTargetlessCharge'
                 AND OLD."AttemptType" IS DISTINCT FROM 'LegacyTargetlessCharge' THEN
                RAISE EXCEPTION 'LegacyTargetlessCharge is retired historical data'
                  USING ERRCODE = '23514';
              END IF;
              IF TG_OP = 'UPDATE'
                 AND NEW."ChargeLedgerEntryId" IS DISTINCT FROM OLD."ChargeLedgerEntryId" THEN
                RAISE EXCEPTION 'TenantPaymentAttempt ChargeLedgerEntryId is immutable'
                  USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END;
            $function$;

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_legacy_intent_insert
              ON "TenantPaymentAttempts";
            CREATE TRIGGER trg_tenant_payment_attempt_legacy_intent_insert
              BEFORE INSERT ON "TenantPaymentAttempts"
              FOR EACH ROW
              EXECUTE FUNCTION rc_enforce_payment_attempt_intent();

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_charge_target_immutable
              ON "TenantPaymentAttempts";
            CREATE TRIGGER trg_tenant_payment_attempt_charge_target_immutable
              BEFORE UPDATE OF "ChargeLedgerEntryId", "AttemptType" ON "TenantPaymentAttempts"
              FOR EACH ROW
              EXECUTE FUNCTION rc_enforce_payment_attempt_intent();
            """);

    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_legacy_intent_insert
              ON "TenantPaymentAttempts";
            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_charge_target_immutable
              ON "TenantPaymentAttempts";
            DROP FUNCTION IF EXISTS rc_enforce_payment_attempt_intent();
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_TenantPaymentAttempts_TenantLedgerEntries_ChargeLedgerEntryId_TenantAccountId_PortfolioId",
            table: "TenantPaymentAttempts");
        migrationBuilder.DropIndex(
            name: "IX_TenantPaymentAttempts_ChargeLedgerEntryId_TenantAccountId_PortfolioId",
            table: "TenantPaymentAttempts");
        migrationBuilder.DropCheckConstraint(
            name: "CK_TenantPaymentAttempt_ChargeTarget",
            table: "TenantPaymentAttempts");
        migrationBuilder.DropCheckConstraint(
            name: "CK_TenantPaymentAttempt_Type",
            table: "TenantPaymentAttempts");
        migrationBuilder.DropCheckConstraint(
            name: "CK_TenantPaymentAttempt_Amount",
            table: "TenantPaymentAttempts");
        migrationBuilder.Sql(
            """
            CREATE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
              IF TG_OP IS DISTINCT FROM 'UPDATE'
                 OR current_setting(
                      'rental_command.tenant_payment_attempt_exact_target_migration',
                      true) IS DISTINCT FROM 'on'
                 OR OLD."AttemptType" NOT IN (
                      'UnappliedReceipt',
                      'DepositReceipt',
                      'ImportedReceipt',
                      'LegacyTargetlessCharge')
                 OR NEW."AttemptType" IS DISTINCT FROM 'Charge'
                 OR OLD."ChargeLedgerEntryId" IS NOT NULL
                 OR NEW."ChargeLedgerEntryId" IS NOT NULL
                 OR (to_jsonb(NEW) - 'AttemptType')
                    IS DISTINCT FROM
                    (to_jsonb(OLD) - 'AttemptType') THEN
                RAISE EXCEPTION 'TenantPaymentAttempt rollback rewrite is not an exact supported shape'
                  USING ERRCODE = '23514';
              END IF;

              RETURN NEW;
            END;
            $function$;

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_restore_guard',
              CASE
                WHEN to_regprocedure('rc_guard_tenant_payment_attempt_write()') IS NOT NULL
                 AND EXISTS (
                      SELECT 1
                      FROM pg_trigger
                      WHERE tgrelid = '"TenantPaymentAttempts"'::regclass
                        AND tgname = 'trg_tenant_payment_attempt_write'
                        AND tgfoid =
                            to_regprocedure('rc_guard_tenant_payment_attempt_write()')
                        AND NOT tgisinternal)
                THEN 'on'
                ELSE 'off'
              END,
              true);

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write
              ON "TenantPaymentAttempts";
            CREATE TRIGGER trg_tenant_payment_attempt_write
              BEFORE INSERT OR UPDATE ON "TenantPaymentAttempts"
              FOR EACH ROW
              EXECUTE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration();

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_migration',
              'on',
              true);

            UPDATE "TenantPaymentAttempts"
               SET "AttemptType" = 'Charge'
             WHERE "AttemptType" IN (
                'UnappliedReceipt',
                'DepositReceipt',
                'ImportedReceipt',
                'LegacyTargetlessCharge'
             );

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_migration',
              'off',
              true);

            DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write
              ON "TenantPaymentAttempts";

            DO $restore_guard$
            BEGIN
              IF current_setting(
                   'rental_command.tenant_payment_attempt_exact_target_restore_guard',
                   true) = 'on' THEN
                EXECUTE $trigger$
                  CREATE TRIGGER trg_tenant_payment_attempt_write
                    BEFORE INSERT OR UPDATE ON "TenantPaymentAttempts"
                    FOR EACH ROW
                    EXECUTE FUNCTION rc_guard_tenant_payment_attempt_write()
                $trigger$;
              END IF;
            END;
            $restore_guard$;

            SELECT set_config(
              'rental_command.tenant_payment_attempt_exact_target_restore_guard',
              'off',
              true);

            DROP FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration();
            """);
        migrationBuilder.AddCheckConstraint(
            name: "CK_TenantPaymentAttempt_Type",
            table: "TenantPaymentAttempts",
            sql: "\"AttemptType\" IN ('Charge', 'Refund', 'Verification')");
        migrationBuilder.AddCheckConstraint(
            name: "CK_TenantPaymentAttempt_Amount",
            table: "TenantPaymentAttempts",
            sql: "(\"AttemptType\" = 'Verification' AND \"Amount\" = 0) OR " +
                 "(\"AttemptType\" IN ('Charge','Refund') AND \"Amount\" > 0)");
        migrationBuilder.AlterColumn<string>(
            name: "AttemptType",
            table: "TenantPaymentAttempts",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(30)",
            oldMaxLength: 30);
        migrationBuilder.DropColumn(
            name: "ChargeLedgerEntryId",
            table: "TenantPaymentAttempts");
    }
}
