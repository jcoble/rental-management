namespace RentalCommand.Data;

/// <summary>
/// PostgreSQL functions and triggers that enforce the canonical TenantAccount and append-only money
/// contract. Migrations install these statements after creating the mapped tables and remove them in
/// reverse order. Deferred constraint triggers intentionally validate the final transaction state so
/// atomic commands may append balancing rows before commit.
/// </summary>
internal static class TenantAccountPostgreSqlContract
{
    internal static IReadOnlyList<string> InitialCreateStatements { get; } =
    [
        CreateAppendOnlyGuard,
        CreateTenantAccountCurrencyGuard,
        TenantAccountCloseValidatorStatement(allowPlannedCancellation: true),
        CreateConditionPeriodGuard,
        CreatePaymentAttemptWriteGuard,
        CreatePaymentAttemptClaimFunction,
        CreatePaymentAttemptExactClaimFunction,
        CreatePaymentAttemptTransitionFunction,
        CreatePaymentAttemptValidator,
        CreateProviderPaymentFenceAssertion,
        CreateOpenAccountWriteGuard,
        CreateLedgerEntryValidator,
        CreatePaymentAttemptSuccessValidator,
        CreateLedgerAllocationValidator,
        CreateAutopayValidator,
        CreateSecurityDepositAccountValidator,
        CreateSecurityDepositEntryValidator,
        CreateTriggers,
    ];

    internal static IReadOnlyList<string> CreateStatements { get; } =
    [
        CreateAppendOnlyGuard,
        CreateTenantAccountCurrencyGuard,
        TenantAccountCloseValidatorStatement(allowPlannedCancellation: true),
        CreateConditionPeriodGuard,
        CreatePaymentAttemptWriteGuard,
        CreatePaymentAttemptIntentGuard,
        CreatePaymentAttemptClaimFunction,
        CreatePaymentAttemptExactClaimFunction,
        CreatePaymentAttemptTransitionFunction,
        CreatePaymentAttemptReconciliationScheduleFunction,
        CreatePaymentAttemptValidator,
        CreateProviderPaymentFenceAssertion,
        CreateOpenAccountWriteGuard,
        CreateLedgerEntryValidator,
        CreatePaymentAttemptSuccessValidator,
        CreateLedgerAllocationValidator,
        CreateAutopayValidator,
        CreateSecurityDepositAccountValidator,
        CreateSecurityDepositEntryValidator,
        CreateTriggers,
        CreatePaymentAttemptIntentTriggers,
    ];

    /// <summary>
    /// Exact contract that existed immediately before the durable provider-fence migration.
    /// Down uses this after removing the fence-aware objects and columns; keeping it explicit
    /// prevents a rollback from leaving a function/trigger that references dropped columns.
    /// </summary>
    internal static IReadOnlyList<string> PreFenceCreateStatements { get; } =
    [
        CreateAppendOnlyGuard,
        CreateTenantAccountCurrencyGuard,
        TenantAccountCloseValidatorStatement(allowPlannedCancellation: true),
        CreateConditionPeriodGuard,
        CreatePaymentAttemptWriteGuard,
        CreatePaymentAttemptIntentGuard,
        CreatePaymentAttemptClaimFunction,
        CreatePaymentAttemptExactClaimFunction,
        CreatePaymentAttemptTransitionFunctionBeforeFence,
        CreatePaymentAttemptValidator,
        CreateOpenAccountWriteGuardBeforeFence,
        CreateLedgerEntryValidator,
        CreatePaymentAttemptSuccessValidator,
        CreateLedgerAllocationValidator,
        CreateAutopayValidator,
        CreateSecurityDepositAccountValidator,
        CreateSecurityDepositEntryValidator,
        CreateTriggersBeforeFence,
        CreatePaymentAttemptIntentTriggers,
    ];

    internal static string TenantAccountCloseValidatorStatement(bool allowPlannedCancellation)
    {
        if (allowPlannedCancellation)
        {
            return CreateTenantAccountCloseValidator;
        }

        return CreateTenantAccountCloseValidator.Replace(
            """
              IF account_close_reason IS DISTINCT FROM 'PlannedRelationshipCanceled'
                 AND (possession_returned_at IS NULL OR possession_returned_at > account_closed_at) THEN
            """,
            """
              IF possession_returned_at IS NULL OR possession_returned_at > account_closed_at THEN
            """,
            StringComparison.Ordinal);
    }

    internal static string PaymentAttemptValidatorStatement(
        bool allowUnappliedReceiptRefund)
    {
        if (allowUnappliedReceiptRefund)
        {
            return CreatePaymentAttemptValidator;
        }

        return CreatePaymentAttemptValidator.Replace(
            """
               OR original_attempt."AttemptType" NOT IN ('Charge', 'UnappliedReceipt')
            """,
            """
               OR original_attempt."AttemptType" <> 'Charge'
            """,
            StringComparison.Ordinal);
    }

    internal static string InteractivePaymentReconciliationScheduleCreateStatement =>
        CreatePaymentAttemptReconciliationScheduleFunction;

    internal const string InteractivePaymentReconciliationScheduleDropStatement =
        "DROP FUNCTION IF EXISTS rc_schedule_tenant_payment_reconciliation(bigint, integer, integer, varchar, varchar, uuid, timestamp with time zone, varchar, varchar);";

    internal static IReadOnlyList<string> DropStatements { get; } =
    [
        DropTriggers,
        DropFunctions,
    ];

    private const string CreateAppendOnlyGuard = """
        CREATE OR REPLACE FUNCTION rc_reject_append_only_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF TG_OP = 'DELETE'
             AND rc_sandbox_graduation_allows((to_jsonb(OLD) ->> 'PortfolioId')::integer) THEN
            RETURN OLD;
          END IF;

          RAISE EXCEPTION '% is append-only; % is not permitted', TG_TABLE_NAME, TG_OP
            USING ERRCODE = '23514';
        END;
        $function$;
        """;

    private const string CreateTenantAccountCurrencyGuard = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_account_currency()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          portfolio_currency varchar(3);
        BEGIN
          IF TG_OP = 'UPDATE' AND NEW."Currency" IS DISTINCT FROM OLD."Currency" THEN
            RAISE EXCEPTION 'TenantAccount %.Currency is immutable', OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          SELECT portfolio."Currency"
            INTO portfolio_currency
          FROM "Portfolios" AS portfolio
          WHERE portfolio."Id" = NEW."PortfolioId";

          IF portfolio_currency IS NULL OR NEW."Currency" IS DISTINCT FROM portfolio_currency THEN
            RAISE EXCEPTION 'TenantAccount currency % does not match Portfolio % currency %',
              NEW."Currency", NEW."PortfolioId", portfolio_currency
              USING ERRCODE = '23514';
          END IF;

          RETURN NEW;
        END;
        $function$;
        """;

    private const string CreateTenantAccountCloseValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_account_close()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_id integer;
          account_portfolio_id integer;
          management_id integer;
          account_closed_at timestamp with time zone;
          account_close_reason text;
          management_closed_at timestamp with time zone;
          possession_returned_at timestamp with time zone;
          receivable_balance numeric(18,2);
          deposit_balance numeric(18,2);
        BEGIN
          IF TG_TABLE_NAME = 'TenantAccounts' THEN
            account_id := NEW."Id";
            account_portfolio_id := NEW."PortfolioId";
            management_id := NEW."LeaseManagementId";
          ELSE
            management_id := NEW."Id";
            account_portfolio_id := NEW."PortfolioId";

            SELECT account."Id"
              INTO account_id
            FROM "TenantAccounts" AS account
            WHERE account."PortfolioId" = account_portfolio_id
              AND account."LeaseManagementId" = management_id;

            IF account_id IS NULL THEN
              IF NEW."AccountClosedAtUtc" IS NOT NULL THEN
                RAISE EXCEPTION 'LeaseManagement % cannot close without its TenantAccount', management_id
                  USING ERRCODE = '23514';
              END IF;
              RETURN NULL;
            END IF;
          END IF;

          SELECT account."ClosedAtUtc",
                 account."CloseReasonCode",
                 management."AccountClosedAtUtc",
                 management."PossessionReturnedAtUtc"
            INTO account_closed_at, account_close_reason, management_closed_at, possession_returned_at
          FROM "TenantAccounts" AS account
          JOIN "LeaseManagements" AS management
            ON management."PortfolioId" = account."PortfolioId"
           AND management."Id" = account."LeaseManagementId"
          WHERE account."PortfolioId" = account_portfolio_id
            AND account."Id" = account_id;

          IF account_closed_at IS DISTINCT FROM management_closed_at THEN
            RAISE EXCEPTION 'TenantAccount % and LeaseManagement % close timestamps must be equal',
              account_id, management_id
              USING ERRCODE = '23514';
          END IF;

          IF account_closed_at IS NULL THEN
            RETURN NULL;
          END IF;

          PERFORM pg_advisory_xact_lock(73001, account_id);

          IF account_close_reason IS DISTINCT FROM 'PlannedRelationshipCanceled'
             AND (possession_returned_at IS NULL OR possession_returned_at > account_closed_at) THEN
            RAISE EXCEPTION 'TenantAccount % cannot close before possession is returned', account_id
              USING ERRCODE = '23514';
          END IF;

          SELECT COALESCE(SUM(
                   CASE entry."Direction" WHEN 'Debit' THEN entry."Amount" ELSE -entry."Amount" END), 0)
            INTO receivable_balance
          FROM "TenantLedgerEntries" AS entry
          WHERE entry."PortfolioId" = account_portfolio_id
            AND entry."TenantAccountId" = account_id;

          IF receivable_balance <> 0 THEN
            RAISE EXCEPTION 'TenantAccount % cannot close with receivable balance %',
              account_id, receivable_balance
              USING ERRCODE = '23514';
          END IF;

          SELECT COALESCE(SUM(
                   CASE entry."Direction" WHEN 'Increase' THEN entry."Amount" ELSE -entry."Amount" END), 0)
            INTO deposit_balance
          FROM "SecurityDepositAccounts" AS deposit_account
          JOIN "SecurityDepositEntries" AS entry
            ON entry."PortfolioId" = deposit_account."PortfolioId"
           AND entry."SecurityDepositAccountId" = deposit_account."Id"
          WHERE deposit_account."PortfolioId" = account_portfolio_id
            AND deposit_account."TenantAccountId" = account_id;

          IF deposit_balance <> 0 THEN
            RAISE EXCEPTION 'TenantAccount % cannot close with security-deposit balance %',
              account_id, deposit_balance
              USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateConditionPeriodGuard = """
        CREATE OR REPLACE FUNCTION rc_guard_tenant_account_condition_period()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            IF rc_sandbox_graduation_allows(OLD."PortfolioId") THEN
              RETURN OLD;
            END IF;

            RAISE EXCEPTION 'TenantAccountConditionPeriod % cannot be deleted', OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          IF OLD."EndedAtUtc" IS NOT NULL
             OR NEW."EndedAtUtc" IS NULL
             OR (to_jsonb(NEW) - 'EndedAtUtc') IS DISTINCT FROM (to_jsonb(OLD) - 'EndedAtUtc') THEN
            RAISE EXCEPTION 'TenantAccountConditionPeriod % permits only one null-to-value EndedAtUtc transition',
              OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          RETURN NEW;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptWriteGuard = """
        CREATE OR REPLACE FUNCTION rc_guard_tenant_payment_attempt_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_currency varchar(3);
        BEGIN
          SELECT account."Currency"
            INTO account_currency
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = NEW."TenantAccountId";

          IF account_currency IS NULL OR NEW."Currency" IS DISTINCT FROM account_currency THEN
            RAISE EXCEPTION 'TenantPaymentAttempt currency/account mismatch for TenantAccount %',
              NEW."TenantAccountId"
              USING ERRCODE = '23514';
          END IF;

          IF TG_OP = 'UPDATE'
             AND current_setting('rental_command.tenant_payment_attempt_write', true) IS DISTINCT FROM 'on' THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % must change through a fenced database function', OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          IF TG_OP = 'UPDATE' AND (
               NEW."Id" IS DISTINCT FROM OLD."Id"
            OR NEW."PublicId" IS DISTINCT FROM OLD."PublicId"
            OR NEW."PortfolioId" IS DISTINCT FROM OLD."PortfolioId"
            OR NEW."TenantAccountId" IS DISTINCT FROM OLD."TenantAccountId"
            OR NEW."Provider" IS DISTINCT FROM OLD."Provider"
            OR (OLD."ProviderObjectId" IS NOT NULL
                AND NEW."ProviderObjectId" IS DISTINCT FROM OLD."ProviderObjectId")
            OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
            OR NEW."AttemptType" IS DISTINCT FROM OLD."AttemptType"
            OR NEW."RefundsPaymentAttemptId" IS DISTINCT FROM OLD."RefundsPaymentAttemptId"
            OR NEW."Amount" IS DISTINCT FROM OLD."Amount"
            OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
            OR NEW."PreparedAtUtc" IS DISTINCT FROM OLD."PreparedAtUtc"
            OR NEW."CreatedByUserId" IS DISTINCT FROM OLD."CreatedByUserId") THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % immutable identity/value fields cannot change', OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          RETURN NEW;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptIntentGuard = """
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
        REVOKE EXECUTE ON FUNCTION rc_enforce_payment_attempt_intent() FROM PUBLIC;
        """;

    private const string CreatePaymentAttemptClaimFunction = """
        CREATE OR REPLACE FUNCTION rc_claim_tenant_payment_attempt(
          p_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_claim_owner varchar(200),
          p_lease_duration interval DEFAULT interval '5 minutes')
        RETURNS uuid
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          claimed_token uuid;
          now_utc timestamp with time zone := clock_timestamp();
        BEGIN
          IF NULLIF(btrim(p_claim_owner), '') IS NULL OR p_lease_duration <= interval '0 seconds' THEN
            RAISE EXCEPTION 'Payment-attempt claim owner and positive lease duration are required'
              USING ERRCODE = '22023';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);

          UPDATE "TenantPaymentAttempts" AS attempt
          SET "ClaimOwner" = p_claim_owner,
              "ClaimToken" = gen_random_uuid(),
              "ClaimExpiresAtUtc" = now_utc + p_lease_duration,
              "AttemptCount" = attempt."AttemptCount" + 1,
              "UpdatedAtUtc" = now_utc
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."State" IN ('Prepared','Submitted','Failed','Unknown')
            AND (attempt."NextAttemptAtUtc" IS NULL OR attempt."NextAttemptAtUtc" <= now_utc)
            AND (attempt."ClaimToken" IS NULL OR attempt."ClaimExpiresAtUtc" <= now_utc)
          RETURNING attempt."ClaimToken" INTO claimed_token;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);
          RETURN claimed_token;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptTransitionFunction = """
        CREATE OR REPLACE FUNCTION rc_transition_tenant_payment_attempt(
          p_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_claim_token uuid,
          p_new_state varchar(30),
          p_provider_object_id varchar(200) DEFAULT NULL,
          p_failure_code varchar(100) DEFAULT NULL,
          p_failure_reason varchar(2000) DEFAULT NULL,
          p_next_attempt_at_utc timestamp with time zone DEFAULT NULL,
          p_provider_fence_token uuid DEFAULT NULL)
        RETURNS boolean
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          prior_state varchar(30);
          prior_provider_object_id varchar(200);
          prior_fence_token uuid;
          now_utc timestamp with time zone := clock_timestamp();
          changed_count integer;
        BEGIN
          IF p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown') THEN
            RAISE EXCEPTION 'Unsupported TenantPaymentAttempt transition state %', p_new_state
              USING ERRCODE = '22023';
          END IF;

          SELECT attempt."State", attempt."ProviderObjectId", attempt."ProviderFenceToken"
            INTO prior_state, prior_provider_object_id, prior_fence_token
          FROM "TenantPaymentAttempts" AS attempt
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."ClaimToken" = p_claim_token
            AND attempt."ClaimExpiresAtUtc" > now_utc
          FOR UPDATE;

          IF prior_state IS NULL THEN
            RETURN false;
          END IF;

          IF prior_provider_object_id IS NOT NULL
             AND p_provider_object_id IS NOT NULL
             AND prior_provider_object_id IS DISTINCT FROM p_provider_object_id THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % is already bound to provider object %',
              p_id, prior_provider_object_id
              USING ERRCODE = '23514';
          END IF;

          IF p_new_state = 'Submitted'
             AND prior_state = 'Prepared'
             AND p_provider_fence_token IS NULL THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % requires its durable provider fence before submission', p_id
              USING ERRCODE = '23514';
          END IF;
          IF p_new_state = 'Submitted'
             AND prior_state = 'Submitted'
             AND prior_fence_token IS NOT NULL
             AND prior_fence_token IS DISTINCT FROM p_provider_fence_token THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % provider fence does not match its durable reservation', p_id
              USING ERRCODE = '23514';
          END IF;

          IF prior_state = 'Succeeded'
             OR prior_state = 'Canceled'
             OR (prior_state = 'Failed' AND p_new_state = 'Submitted')
             OR (prior_state = 'Prepared' AND p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown'))
             OR (prior_state IN ('Submitted','Failed','Unknown')
                 AND p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown')) THEN
            RAISE EXCEPTION 'Invalid TenantPaymentAttempt transition from % to %', prior_state, p_new_state
              USING ERRCODE = '23514';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);

          UPDATE "TenantPaymentAttempts" AS attempt
          SET "State" = p_new_state,
              "ProviderObjectId" = COALESCE(attempt."ProviderObjectId", p_provider_object_id),
              "ProviderFenceToken" = CASE
                WHEN p_new_state = 'Submitted' THEN COALESCE(attempt."ProviderFenceToken", p_provider_fence_token)
                ELSE NULL
              END,
              "ProviderFenceAcquiredAtUtc" = CASE
                WHEN p_new_state = 'Submitted' THEN COALESCE(attempt."ProviderFenceAcquiredAtUtc", now_utc)
                ELSE NULL
              END,
              "SubmittedAtUtc" = CASE
                WHEN p_new_state IN ('Submitted','Succeeded')
                  THEN COALESCE(attempt."SubmittedAtUtc", now_utc)
                ELSE attempt."SubmittedAtUtc"
              END,
              "SettledAtUtc" = CASE WHEN p_new_state = 'Succeeded' THEN now_utc ELSE NULL END,
              "FailureCode" = CASE WHEN p_new_state = 'Succeeded' THEN NULL ELSE p_failure_code END,
              "FailureReason" = CASE WHEN p_new_state = 'Succeeded' THEN NULL ELSE p_failure_reason END,
              "NextAttemptAtUtc" = CASE
                WHEN p_new_state IN ('Failed','Unknown') THEN p_next_attempt_at_utc
                ELSE NULL
              END,
              "ClaimOwner" = NULL,
              "ClaimToken" = NULL,
              "ClaimExpiresAtUtc" = NULL,
              "UpdatedAtUtc" = now_utc
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."ClaimToken" = p_claim_token
            AND attempt."ClaimExpiresAtUtc" > now_utc;

          GET DIAGNOSTICS changed_count = ROW_COUNT;
          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);
          RETURN changed_count = 1;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptExactClaimFunction = """
        CREATE OR REPLACE FUNCTION rc_claim_exact_tenant_payment_attempt(
          p_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_claim_owner varchar(200),
          p_lease_duration interval DEFAULT interval '5 minutes')
        RETURNS uuid
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          claimed_token uuid;
          now_utc timestamp with time zone := clock_timestamp();
        BEGIN
          IF NULLIF(btrim(p_claim_owner), '') IS NULL OR p_lease_duration <= interval '0 seconds' THEN
            RAISE EXCEPTION 'Payment-attempt claim owner and positive lease duration are required'
              USING ERRCODE = '22023';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);

          UPDATE "TenantPaymentAttempts" AS attempt
          SET "ClaimOwner" = p_claim_owner,
              "ClaimToken" = gen_random_uuid(),
              "ClaimExpiresAtUtc" = now_utc + p_lease_duration,
              "AttemptCount" = CASE
                WHEN attempt."State" = 'Prepared' AND attempt."ProviderObjectId" IS NULL
                  THEN attempt."AttemptCount" + 1
                ELSE attempt."AttemptCount"
              END,
              "UpdatedAtUtc" = now_utc
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."State" IN ('Prepared','Submitted','Failed','Unknown')
            AND (attempt."ClaimToken" IS NULL OR attempt."ClaimExpiresAtUtc" <= now_utc)
          RETURNING attempt."ClaimToken" INTO claimed_token;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);
          RETURN claimed_token;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptReconciliationScheduleFunction = """
        CREATE OR REPLACE FUNCTION rc_schedule_tenant_payment_reconciliation(
          p_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_provider varchar(100),
          p_idempotency_key varchar(200),
          p_provider_fence_token uuid,
          p_next_attempt_at_utc timestamp with time zone,
          p_failure_code varchar(100),
          p_failure_reason varchar(2000))
        RETURNS boolean
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          changed_count integer;
          now_utc timestamp with time zone := clock_timestamp();
        BEGIN
          IF p_next_attempt_at_utc IS NULL OR p_next_attempt_at_utc <= now_utc THEN
            RAISE EXCEPTION 'Interactive payment reconciliation must schedule a future attempt'
              USING ERRCODE = '22023';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);

          UPDATE "TenantPaymentAttempts" AS attempt
          SET "NextAttemptAtUtc" = p_next_attempt_at_utc,
              "FailureCode" = p_failure_code,
              "FailureReason" = p_failure_reason,
              "ClaimOwner" = NULL,
              "ClaimToken" = NULL,
              "ClaimExpiresAtUtc" = NULL,
              "UpdatedAtUtc" = now_utc
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."Provider" = p_provider
            AND attempt."IdempotencyKey" = p_idempotency_key
            AND attempt."State" IN ('Prepared','Submitted')
            AND attempt."ProviderFenceToken" IS NOT DISTINCT FROM p_provider_fence_token;

          GET DIAGNOSTICS changed_count = ROW_COUNT;
          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);
          RETURN changed_count = 1;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_payment_attempt()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_currency varchar(3);
          original_attempt record;
        BEGIN
          SELECT account."Currency"
            INTO account_currency
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = NEW."TenantAccountId";

          IF account_currency IS NULL OR NEW."Currency" IS DISTINCT FROM account_currency THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % does not match TenantAccount scope/currency', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          IF NEW."AttemptType" = 'Refund' THEN
            SELECT source."AttemptType", source."State", source."Amount", source."Currency",
                   source."Provider", source."ProviderObjectId"
              INTO original_attempt
            FROM "TenantPaymentAttempts" AS source
            WHERE source."Id" = NEW."RefundsPaymentAttemptId"
              AND source."PortfolioId" = NEW."PortfolioId"
              AND source."TenantAccountId" = NEW."TenantAccountId";

            IF NOT FOUND
               OR original_attempt."AttemptType" NOT IN ('Charge', 'UnappliedReceipt')
               OR original_attempt."State" <> 'Succeeded'
               OR original_attempt."Amount" IS DISTINCT FROM NEW."Amount"
               OR original_attempt."Currency" IS DISTINCT FROM NEW."Currency"
               OR original_attempt."Provider" IS DISTINCT FROM NEW."Provider"
               OR (original_attempt."Provider" <> 'manual'
                   AND original_attempt."ProviderObjectId" IS NULL) THEN
              RAISE EXCEPTION 'Refund TenantPaymentAttempt % lacks exact settled receipt provenance', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptTransitionFunctionBeforeFence = """
        CREATE OR REPLACE FUNCTION rc_transition_tenant_payment_attempt(
          p_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_claim_token uuid,
          p_new_state varchar(30),
          p_provider_object_id varchar(200) DEFAULT NULL,
          p_failure_code varchar(100) DEFAULT NULL,
          p_failure_reason varchar(2000) DEFAULT NULL,
          p_next_attempt_at_utc timestamp with time zone DEFAULT NULL)
        RETURNS boolean
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          prior_state varchar(30);
          prior_provider_object_id varchar(200);
          now_utc timestamp with time zone := clock_timestamp();
          changed_count integer;
        BEGIN
          IF p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown') THEN
            RAISE EXCEPTION 'Unsupported TenantPaymentAttempt transition state %', p_new_state
              USING ERRCODE = '22023';
          END IF;

          SELECT attempt."State", attempt."ProviderObjectId"
            INTO prior_state, prior_provider_object_id
          FROM "TenantPaymentAttempts" AS attempt
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."ClaimToken" = p_claim_token
            AND attempt."ClaimExpiresAtUtc" > now_utc
          FOR UPDATE;

          IF prior_state IS NULL THEN
            RETURN false;
          END IF;

          IF prior_provider_object_id IS NOT NULL
             AND p_provider_object_id IS NOT NULL
             AND prior_provider_object_id IS DISTINCT FROM p_provider_object_id THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % is already bound to provider object %',
              p_id, prior_provider_object_id
              USING ERRCODE = '23514';
          END IF;

          IF prior_state = 'Succeeded'
             OR prior_state = 'Canceled'
             OR (prior_state = 'Failed' AND p_new_state = 'Submitted')
             OR (prior_state = 'Prepared' AND p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown'))
             OR (prior_state IN ('Submitted','Failed','Unknown')
                 AND p_new_state NOT IN ('Submitted','Succeeded','Failed','Canceled','Unknown')) THEN
            RAISE EXCEPTION 'Invalid TenantPaymentAttempt transition from % to %', prior_state, p_new_state
              USING ERRCODE = '23514';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);

          UPDATE "TenantPaymentAttempts" AS attempt
          SET "State" = p_new_state,
              "ProviderObjectId" = COALESCE(attempt."ProviderObjectId", p_provider_object_id),
              "SubmittedAtUtc" = CASE
                WHEN p_new_state IN ('Submitted','Succeeded')
                  THEN COALESCE(attempt."SubmittedAtUtc", now_utc)
                ELSE attempt."SubmittedAtUtc"
              END,
              "SettledAtUtc" = CASE WHEN p_new_state = 'Succeeded' THEN now_utc ELSE NULL END,
              "FailureCode" = CASE WHEN p_new_state = 'Succeeded' THEN NULL ELSE p_failure_code END,
              "FailureReason" = CASE WHEN p_new_state = 'Succeeded' THEN NULL ELSE p_failure_reason END,
              "NextAttemptAtUtc" = CASE
                WHEN p_new_state IN ('Failed','Unknown') THEN p_next_attempt_at_utc
                ELSE NULL
              END,
              "ClaimOwner" = NULL,
              "ClaimToken" = NULL,
              "ClaimExpiresAtUtc" = NULL,
              "UpdatedAtUtc" = now_utc
          WHERE attempt."Id" = p_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."ClaimToken" = p_claim_token
            AND attempt."ClaimExpiresAtUtc" > now_utc;

          GET DIAGNOSTICS changed_count = ROW_COUNT;
          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);
          RETURN changed_count = 1;
        END;
        $function$;
        """;

    private const string CreateOpenAccountWriteGuardBeforeFence = """
        CREATE OR REPLACE FUNCTION rc_guard_open_tenant_account_money_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_id integer;
          account_closed_at timestamp with time zone;
        BEGIN
          IF TG_TABLE_NAME = 'TenantLedgerEntries' THEN
            account_id := NEW."TenantAccountId";
          ELSE
            SELECT deposit_account."TenantAccountId"
              INTO account_id
            FROM "SecurityDepositAccounts" AS deposit_account
            WHERE deposit_account."PortfolioId" = NEW."PortfolioId"
              AND deposit_account."Id" = NEW."SecurityDepositAccountId";
          END IF;

          IF account_id IS NULL THEN
            RETURN NEW;
          END IF;

          PERFORM pg_advisory_xact_lock(73001, account_id);

          SELECT account."ClosedAtUtc"
            INTO account_closed_at
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = account_id;

          IF account_closed_at IS NOT NULL THEN
            RAISE EXCEPTION 'TenantAccount % is closed; new money rows are not permitted', account_id
              USING ERRCODE = '23514';
          END IF;

          RETURN NEW;
        END;
        $function$;
        """;

    private static string CreateTriggersBeforeFence =>
        CreateTriggers.Replace(
"""
        CREATE TRIGGER trg_tenant_ledger_allocation_open_account
        BEFORE INSERT ON "TenantLedgerAllocations"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_open_tenant_account_money_write();

""",
            string.Empty,
            StringComparison.Ordinal);

    private const string CreateProviderPaymentFenceAssertion = """
        CREATE OR REPLACE FUNCTION rc_assert_provider_payment_fence(
          p_payment_attempt_id bigint,
          p_tenant_account_id integer,
          p_portfolio_id integer,
          p_provider_fence_token uuid)
        RETURNS bigint
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          reserved_attempt_id bigint;
        BEGIN
          SELECT attempt."Id"
            INTO reserved_attempt_id
          FROM "TenantPaymentAttempts" AS attempt
          WHERE attempt."Id" = p_payment_attempt_id
            AND attempt."TenantAccountId" = p_tenant_account_id
            AND attempt."PortfolioId" = p_portfolio_id
            AND attempt."State" = 'Submitted'
            AND attempt."ProviderFenceToken" = p_provider_fence_token
          FOR UPDATE;

          IF reserved_attempt_id IS NULL THEN
            RAISE EXCEPTION 'Provider payment attempt % does not own its durable fence',
              p_payment_attempt_id
              USING ERRCODE = '23514';
          END IF;

          PERFORM set_config(
            'rental_command.provider_payment_attempt_id', reserved_attempt_id::text, true);
          PERFORM set_config(
            'rental_command.provider_payment_fence_token', p_provider_fence_token::text, true);
          RETURN reserved_attempt_id;
        END;
        $function$;
        """;

    private const string CreateOpenAccountWriteGuard = """
        CREATE OR REPLACE FUNCTION rc_guard_open_tenant_account_money_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_id integer;
          account_closed_at timestamp with time zone;
          provider_attempt_id bigint;
          provider_charge_id bigint;
          provider_name varchar(100);
          provider_attempt_state varchar(50);
          submitted_attempt_id bigint;
          authorized_provider_attempt_id bigint;
          authorized_provider_fence_token uuid;
        BEGIN
          IF NULLIF(current_setting('rental_command.provider_payment_attempt_id', true), '') IS NOT NULL THEN
            authorized_provider_attempt_id :=
              NULLIF(current_setting('rental_command.provider_payment_attempt_id', true), '')::bigint;
            authorized_provider_fence_token :=
              NULLIF(current_setting('rental_command.provider_payment_fence_token', true), '')::uuid;
          END IF;

          IF TG_TABLE_NAME = 'TenantLedgerEntries' THEN
            account_id := NEW."TenantAccountId";
            provider_attempt_id := NEW."ProviderPaymentAttemptId";
            IF provider_attempt_id IS NOT NULL THEN
              SELECT attempt."ChargeLedgerEntryId"
                     , attempt."Provider"
                     , attempt."State"
                INTO provider_charge_id
                     , provider_name
                     , provider_attempt_state
              FROM "TenantPaymentAttempts" AS attempt
              WHERE attempt."Id" = provider_attempt_id
                AND attempt."PortfolioId" = NEW."PortfolioId"
                AND attempt."TenantAccountId" = account_id;
            END IF;
          ELSIF TG_TABLE_NAME = 'TenantLedgerAllocations' THEN
            SELECT credit."TenantAccountId"
                   , credit."ProviderPaymentAttemptId"
              INTO account_id
                   , provider_attempt_id
            FROM "TenantLedgerEntries" AS credit
            WHERE credit."PortfolioId" = NEW."PortfolioId"
              AND credit."Id" = NEW."CreditEntryId";
          ELSE
            SELECT deposit_account."TenantAccountId"
              INTO account_id
            FROM "SecurityDepositAccounts" AS deposit_account
            WHERE deposit_account."PortfolioId" = NEW."PortfolioId"
              AND deposit_account."Id" = NEW."SecurityDepositAccountId";
          END IF;

          IF account_id IS NULL THEN
            RETURN NEW;
          END IF;

          IF authorized_provider_attempt_id IS NOT NULL
             AND NOT EXISTS (
               SELECT 1
               FROM "TenantPaymentAttempts" AS authorized
               WHERE authorized."Id" = authorized_provider_attempt_id
                 AND authorized."PortfolioId" = NEW."PortfolioId"
                 AND authorized."TenantAccountId" = account_id
                 AND authorized."State" = 'Submitted'
                 AND authorized."ProviderFenceToken" = authorized_provider_fence_token) THEN
            RAISE EXCEPTION 'Provider payment attempt % no longer owns its durable fence',
              authorized_provider_attempt_id
              USING ERRCODE = '23514';
          END IF;

          IF provider_attempt_id IS NOT NULL AND provider_charge_id IS NULL THEN
            SELECT attempt."ChargeLedgerEntryId", attempt."Provider", attempt."State"
              INTO provider_charge_id, provider_name, provider_attempt_state
            FROM "TenantPaymentAttempts" AS attempt
            WHERE attempt."Id" = provider_attempt_id
              AND attempt."PortfolioId" = NEW."PortfolioId"
              AND attempt."TenantAccountId" = account_id;
          END IF;

          IF provider_attempt_id IS NOT NULL
             AND authorized_provider_attempt_id IS DISTINCT FROM provider_attempt_id THEN
            IF provider_name IS NULL
               OR provider_attempt_state IN ('Prepared', 'Submitted') THEN
              RAISE EXCEPTION 'Provider payment attempt % requires its durable fence',
                provider_attempt_id
                USING ERRCODE = '23514';
            END IF;
            IF provider_name <> 'manual'
               AND provider_attempt_state IS DISTINCT FROM 'Succeeded' THEN
              RAISE EXCEPTION 'Provider payment attempt % requires its durable fence',
                provider_attempt_id
                USING ERRCODE = '23514';
            END IF;
          END IF;

          PERFORM pg_advisory_xact_lock(73001, account_id);

          SELECT account."ClosedAtUtc"
            INTO account_closed_at
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = account_id;

          IF account_closed_at IS NOT NULL THEN
            RAISE EXCEPTION 'TenantAccount % is closed; new money rows are not permitted', account_id
              USING ERRCODE = '23514';
          END IF;

          -- A Submitted attempt owns the charge-level fence. Every non-provider money write
          -- must stop rather than send a stale balance to the provider. Prepared reservations
          -- are explicitly canceled here so a cheque/receipt committed between prepare and
          -- provider create makes the later submit return a terminal Canceled state.
          SELECT attempt."Id"
            INTO submitted_attempt_id
          FROM "TenantPaymentAttempts" AS attempt
          WHERE attempt."PortfolioId" = NEW."PortfolioId"
            AND attempt."TenantAccountId" = account_id
            AND attempt."State" = 'Submitted'
            AND (provider_attempt_id IS NULL
                 OR (attempt."Id" <> provider_attempt_id
                     AND attempt."ChargeLedgerEntryId" IS NOT DISTINCT FROM provider_charge_id))
          ORDER BY attempt."Id"
          LIMIT 1;

          IF submitted_attempt_id IS NOT NULL THEN
            RAISE EXCEPTION 'TenantAccount % has an in-progress provider payment attempt %',
              account_id, submitted_attempt_id
              USING ERRCODE = '23514';
          END IF;

          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'on', true);
          UPDATE "TenantPaymentAttempts" AS attempt
          SET "State" = 'Canceled',
              "FailureCode" = 'MONEY_MOVEMENT_RESERVED',
              "FailureReason" = 'Canceled by a committed tenant-account money movement',
              "ProviderFenceToken" = NULL,
              "ProviderFenceAcquiredAtUtc" = NULL,
              "NextAttemptAtUtc" = NULL,
              "UpdatedAtUtc" = clock_timestamp()
          WHERE attempt."PortfolioId" = NEW."PortfolioId"
            AND attempt."TenantAccountId" = account_id
            AND attempt."State" = 'Prepared';
          PERFORM set_config('rental_command.tenant_payment_attempt_write', 'off', true);

          RETURN NEW;
        END;
        $function$;
        """;

    private const string CreateLedgerEntryValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_ledger_entry()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_management_id integer;
          account_currency varchar(3);
          agreement_management_id integer;
          addendum_management_id integer;
          original record;
        BEGIN
          SELECT account."LeaseManagementId", account."Currency"
            INTO account_management_id, account_currency
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = NEW."TenantAccountId";

          IF account_management_id IS NULL OR NEW."Currency" IS DISTINCT FROM account_currency THEN
            RAISE EXCEPTION 'TenantLedgerEntry % does not match TenantAccount scope/currency', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          IF NEW."LeaseAgreementId" IS NOT NULL THEN
            SELECT agreement."LeaseManagementId"
              INTO agreement_management_id
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."PortfolioId" = NEW."PortfolioId"
              AND agreement."Id" = NEW."LeaseAgreementId";

            IF agreement_management_id IS DISTINCT FROM account_management_id THEN
              RAISE EXCEPTION 'TenantLedgerEntry % Agreement does not belong to its TenantAccount relationship',
                NEW."Id" USING ERRCODE = '23514';
            END IF;
          END IF;

          IF NEW."LeaseAddendumId" IS NOT NULL THEN
            SELECT addendum."LeaseManagementId"
              INTO addendum_management_id
            FROM "LeaseAddenda" AS addendum
            WHERE addendum."PortfolioId" = NEW."PortfolioId"
              AND addendum."Id" = NEW."LeaseAddendumId";

            IF addendum_management_id IS DISTINCT FROM account_management_id THEN
              RAISE EXCEPTION 'TenantLedgerEntry % Addendum does not belong to its TenantAccount relationship',
                NEW."Id" USING ERRCODE = '23514';
            END IF;
          END IF;

          IF NEW."ReversesEntryId" IS NOT NULL THEN
            SELECT source."TenantAccountId", source."Currency", source."Amount", source."Direction"
              INTO original
            FROM "TenantLedgerEntries" AS source
            WHERE source."PortfolioId" = NEW."PortfolioId"
              AND source."Id" = NEW."ReversesEntryId";

            IF NOT FOUND THEN
              RAISE EXCEPTION 'TenantLedgerEntry % reversal source does not exist', NEW."Id"
                USING ERRCODE = '23514';
            END IF;

            IF original."TenantAccountId" IS DISTINCT FROM NEW."TenantAccountId"
               OR original."Currency" IS DISTINCT FROM NEW."Currency"
               OR original."Amount" IS DISTINCT FROM NEW."Amount"
               OR original."Direction" = NEW."Direction" THEN
              RAISE EXCEPTION 'TenantLedgerEntry % is not an exact opposite reversal', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreatePaymentAttemptSuccessValidator = """
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
            -- Historical rows may already have their receipt link. The separate intent
            -- guard prevents this retired shape from being inserted or newly selected.
            RETURN NULL;
          ELSIF linked_entry_count <> 0 THEN
            RAISE EXCEPTION 'TenantPaymentAttempt % ledger entry requires a succeeded receipt or Refund',
              attempt."Id" USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateLedgerAllocationValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_ledger_allocation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          debit record;
          credit record;
          original record;
          debit_allocated numeric(18,2);
          credit_allocated numeric(18,2);
        BEGIN
          -- Serialize allocations against both ledger entries without requiring UPDATE
          -- privilege on the append-only ledger table. Lock in a stable order so two
          -- concurrent allocations cannot deadlock or both pass the balance check.
          PERFORM pg_advisory_xact_lock(hashtextextended(
            'rc:tenant-ledger-entry:' || LEAST(NEW."DebitEntryId", NEW."CreditEntryId")::text, 0));
          PERFORM pg_advisory_xact_lock(hashtextextended(
            'rc:tenant-ledger-entry:' || GREATEST(NEW."DebitEntryId", NEW."CreditEntryId")::text, 0));

          SELECT entry."Direction", entry."Amount", entry."Currency"
            INTO debit
          FROM "TenantLedgerEntries" AS entry
          WHERE entry."PortfolioId" = NEW."PortfolioId"
            AND entry."TenantAccountId" = NEW."TenantAccountId"
            AND entry."Id" = NEW."DebitEntryId";

          IF NOT FOUND THEN
            RAISE EXCEPTION 'TenantLedgerAllocation % debit entry does not exist', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          SELECT entry."Direction", entry."Amount", entry."Currency"
            INTO credit
          FROM "TenantLedgerEntries" AS entry
          WHERE entry."PortfolioId" = NEW."PortfolioId"
            AND entry."TenantAccountId" = NEW."TenantAccountId"
            AND entry."Id" = NEW."CreditEntryId";

          IF NOT FOUND THEN
            RAISE EXCEPTION 'TenantLedgerAllocation % credit entry does not exist', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          IF debit."Direction" <> 'Debit'
             OR credit."Direction" <> 'Credit'
             OR debit."Currency" IS DISTINCT FROM credit."Currency" THEN
            RAISE EXCEPTION 'TenantLedgerAllocation % requires same-currency debit and credit entries', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          IF NEW."ReversesAllocationId" IS NOT NULL THEN
            SELECT allocation."DebitEntryId", allocation."CreditEntryId", allocation."Amount"
              INTO original
            FROM "TenantLedgerAllocations" AS allocation
            WHERE allocation."PortfolioId" = NEW."PortfolioId"
              AND allocation."TenantAccountId" = NEW."TenantAccountId"
              AND allocation."Id" = NEW."ReversesAllocationId";

            IF NOT FOUND THEN
              RAISE EXCEPTION 'TenantLedgerAllocation % reversal source does not exist', NEW."Id"
                USING ERRCODE = '23514';
            END IF;

            IF original."DebitEntryId" IS DISTINCT FROM NEW."DebitEntryId"
               OR original."CreditEntryId" IS DISTINCT FROM NEW."CreditEntryId"
               OR NEW."Amount" IS DISTINCT FROM -original."Amount" THEN
              RAISE EXCEPTION 'TenantLedgerAllocation % is not an exact negative reversal', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          SELECT COALESCE(SUM(allocation."Amount"), 0)
            INTO debit_allocated
          FROM "TenantLedgerAllocations" AS allocation
          WHERE allocation."PortfolioId" = NEW."PortfolioId"
            AND allocation."TenantAccountId" = NEW."TenantAccountId"
            AND allocation."DebitEntryId" = NEW."DebitEntryId";

          SELECT COALESCE(SUM(allocation."Amount"), 0)
            INTO credit_allocated
          FROM "TenantLedgerAllocations" AS allocation
          WHERE allocation."PortfolioId" = NEW."PortfolioId"
            AND allocation."TenantAccountId" = NEW."TenantAccountId"
            AND allocation."CreditEntryId" = NEW."CreditEntryId";

          IF debit_allocated < 0 OR debit_allocated > debit."Amount"
             OR credit_allocated < 0 OR credit_allocated > credit."Amount" THEN
            RAISE EXCEPTION 'TenantLedgerAllocation % would over-allocate or make a net allocation negative',
              NEW."Id" USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateAutopayValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_tenant_autopay_enrollment()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          enrollment_business_date date;
          account_management_id integer;
          responsible_party_count integer;
        BEGIN
          SELECT (NEW."EnrolledAtUtc" AT TIME ZONE portfolio."TimeZone")::date,
                 account."LeaseManagementId"
            INTO enrollment_business_date, account_management_id
          FROM "TenantAccounts" AS account
          JOIN "Portfolios" AS portfolio ON portfolio."Id" = account."PortfolioId"
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = NEW."TenantAccountId";

          SELECT COUNT(*)
            INTO responsible_party_count
          FROM "LeaseManagementParties" AS party
          WHERE party."PortfolioId" = NEW."PortfolioId"
            AND party."Id" = NEW."AuthorizingPartyId"
            AND party."LeaseManagementId" = account_management_id
            AND party."Role" IN ('PrimaryTenant','CoTenant','Guarantor')
            AND party."EffectiveFrom" <= enrollment_business_date
            AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= enrollment_business_date);

          IF account_management_id IS NULL OR responsible_party_count <> 1 THEN
            RAISE EXCEPTION 'TenantAutopayEnrollment % requires an effective responsible party in its relationship',
              NEW."Id" USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateSecurityDepositAccountValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_security_deposit_account()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          account_management_id integer;
          account_currency varchar(3);
          agreement_management_id integer;
        BEGIN
          SELECT account."LeaseManagementId", account."Currency"
            INTO account_management_id, account_currency
          FROM "TenantAccounts" AS account
          WHERE account."PortfolioId" = NEW."PortfolioId"
            AND account."Id" = NEW."TenantAccountId";

          SELECT agreement."LeaseManagementId"
            INTO agreement_management_id
          FROM "LeaseAgreements" AS agreement
          WHERE agreement."PortfolioId" = NEW."PortfolioId"
            AND agreement."Id" = NEW."OriginatingAgreementId";

          IF account_management_id IS NULL
             OR agreement_management_id IS DISTINCT FROM account_management_id
             OR NEW."Currency" IS DISTINCT FROM account_currency THEN
            RAISE EXCEPTION 'SecurityDepositAccount % does not match TenantAccount Agreement scope/currency',
              NEW."Id" USING ERRCODE = '23514';
          END IF;

          IF TG_OP = 'UPDATE' THEN
            RAISE EXCEPTION 'SecurityDepositAccount % is immutable', OLD."Id"
              USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateSecurityDepositEntryValidator = """
        CREATE OR REPLACE FUNCTION rc_validate_security_deposit_entry()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          tenant_account_id integer;
          account_management_id integer;
          account_currency varchar(3);
          agreement_management_id integer;
          addendum_management_id integer;
          ledger_account_id integer;
          ledger_currency varchar(3);
          original record;
          held_balance numeric(18,2);
        BEGIN
          SELECT deposit_account."TenantAccountId", account."LeaseManagementId", account."Currency"
            INTO tenant_account_id, account_management_id, account_currency
          FROM "SecurityDepositAccounts" AS deposit_account
          JOIN "TenantAccounts" AS account
            ON account."PortfolioId" = deposit_account."PortfolioId"
           AND account."Id" = deposit_account."TenantAccountId"
          WHERE deposit_account."PortfolioId" = NEW."PortfolioId"
            AND deposit_account."Id" = NEW."SecurityDepositAccountId"
          FOR UPDATE OF deposit_account;

          IF tenant_account_id IS NULL OR NEW."Currency" IS DISTINCT FROM account_currency THEN
            RAISE EXCEPTION 'SecurityDepositEntry % does not match its deposit account currency/scope', NEW."Id"
              USING ERRCODE = '23514';
          END IF;

          IF NEW."LeaseAgreementId" IS NOT NULL THEN
            SELECT agreement."LeaseManagementId"
              INTO agreement_management_id
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."PortfolioId" = NEW."PortfolioId"
              AND agreement."Id" = NEW."LeaseAgreementId";

            IF agreement_management_id IS DISTINCT FROM account_management_id THEN
              RAISE EXCEPTION 'SecurityDepositEntry % Agreement belongs to a different relationship', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          IF NEW."LeaseAddendumId" IS NOT NULL THEN
            SELECT addendum."LeaseManagementId"
              INTO addendum_management_id
            FROM "LeaseAddenda" AS addendum
            WHERE addendum."PortfolioId" = NEW."PortfolioId"
              AND addendum."Id" = NEW."LeaseAddendumId";

            IF addendum_management_id IS DISTINCT FROM account_management_id THEN
              RAISE EXCEPTION 'SecurityDepositEntry % Addendum belongs to a different relationship', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          IF NEW."TenantLedgerEntryId" IS NOT NULL THEN
            SELECT ledger."TenantAccountId", ledger."Currency"
              INTO ledger_account_id, ledger_currency
            FROM "TenantLedgerEntries" AS ledger
            WHERE ledger."PortfolioId" = NEW."PortfolioId"
              AND ledger."Id" = NEW."TenantLedgerEntryId";

            IF ledger_account_id IS DISTINCT FROM tenant_account_id
               OR ledger_currency IS DISTINCT FROM NEW."Currency" THEN
              RAISE EXCEPTION 'SecurityDepositEntry % ledger provenance belongs to a different account/currency',
                NEW."Id" USING ERRCODE = '23514';
            END IF;
          END IF;

          IF NEW."ReversesEntryId" IS NOT NULL THEN
            SELECT source."SecurityDepositAccountId", source."Currency", source."Amount", source."Direction"
              INTO original
            FROM "SecurityDepositEntries" AS source
            WHERE source."PortfolioId" = NEW."PortfolioId"
              AND source."Id" = NEW."ReversesEntryId";

            IF NOT FOUND THEN
              RAISE EXCEPTION 'SecurityDepositEntry % reversal source does not exist', NEW."Id"
                USING ERRCODE = '23514';
            END IF;

            IF original."SecurityDepositAccountId" IS DISTINCT FROM NEW."SecurityDepositAccountId"
               OR original."Currency" IS DISTINCT FROM NEW."Currency"
               OR original."Amount" IS DISTINCT FROM NEW."Amount"
               OR original."Direction" = NEW."Direction" THEN
              RAISE EXCEPTION 'SecurityDepositEntry % is not an exact opposite reversal', NEW."Id"
                USING ERRCODE = '23514';
            END IF;
          END IF;

          SELECT COALESCE(SUM(
                   CASE entry."Direction" WHEN 'Increase' THEN entry."Amount" ELSE -entry."Amount" END), 0)
            INTO held_balance
          FROM "SecurityDepositEntries" AS entry
          WHERE entry."PortfolioId" = NEW."PortfolioId"
            AND entry."SecurityDepositAccountId" = NEW."SecurityDepositAccountId";

          IF held_balance < 0 THEN
            RAISE EXCEPTION 'SecurityDepositEntry % would make held balance negative (%)', NEW."Id", held_balance
              USING ERRCODE = '23514';
          END IF;

          RETURN NULL;
        END;
        $function$;
        """;

    private const string CreateTriggers = """
        CREATE TRIGGER trg_tenant_account_currency
        BEFORE INSERT OR UPDATE ON "TenantAccounts"
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_account_currency();

        CREATE CONSTRAINT TRIGGER trg_tenant_account_close
        AFTER INSERT OR UPDATE ON "TenantAccounts"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_account_close();

        CREATE CONSTRAINT TRIGGER trg_lease_management_account_close
        AFTER INSERT OR UPDATE ON "LeaseManagements"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_account_close();

        CREATE TRIGGER trg_tenant_account_condition_period_guard
        BEFORE UPDATE OR DELETE ON "TenantAccountConditionPeriods"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_tenant_account_condition_period();

        CREATE TRIGGER trg_tenant_payment_attempt_write
        BEFORE INSERT OR UPDATE ON "TenantPaymentAttempts"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_tenant_payment_attempt_write();

        CREATE CONSTRAINT TRIGGER trg_tenant_payment_attempt_validate
        AFTER INSERT OR UPDATE ON "TenantPaymentAttempts"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_payment_attempt();

        CREATE TRIGGER trg_tenant_ledger_entry_append_only
        BEFORE UPDATE OR DELETE ON "TenantLedgerEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_reject_append_only_mutation();

        CREATE TRIGGER trg_tenant_ledger_entry_open_account
        BEFORE INSERT ON "TenantLedgerEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_open_tenant_account_money_write();

        CREATE TRIGGER trg_tenant_ledger_allocation_open_account
        BEFORE INSERT ON "TenantLedgerAllocations"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_open_tenant_account_money_write();

        CREATE CONSTRAINT TRIGGER trg_tenant_ledger_entry_validate
        AFTER INSERT ON "TenantLedgerEntries"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_ledger_entry();

        CREATE CONSTRAINT TRIGGER trg_tenant_payment_attempt_success
        AFTER INSERT OR UPDATE ON "TenantPaymentAttempts"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_payment_attempt_success();

        CREATE CONSTRAINT TRIGGER trg_tenant_ledger_entry_payment_attempt
        AFTER INSERT ON "TenantLedgerEntries"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_payment_attempt_success();

        CREATE TRIGGER trg_tenant_ledger_allocation_append_only
        BEFORE UPDATE OR DELETE ON "TenantLedgerAllocations"
        FOR EACH ROW EXECUTE FUNCTION rc_reject_append_only_mutation();

        CREATE CONSTRAINT TRIGGER trg_tenant_ledger_allocation_validate
        AFTER INSERT ON "TenantLedgerAllocations"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_ledger_allocation();

        CREATE CONSTRAINT TRIGGER trg_tenant_autopay_enrollment_validate
        AFTER INSERT OR UPDATE ON "TenantAutopayEnrollments"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_tenant_autopay_enrollment();

        CREATE CONSTRAINT TRIGGER trg_security_deposit_account_validate
        AFTER INSERT OR UPDATE ON "SecurityDepositAccounts"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_security_deposit_account();

        CREATE TRIGGER trg_security_deposit_entry_append_only
        BEFORE UPDATE OR DELETE ON "SecurityDepositEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_reject_append_only_mutation();

        CREATE TRIGGER trg_security_deposit_entry_open_account
        BEFORE INSERT ON "SecurityDepositEntries"
        FOR EACH ROW EXECUTE FUNCTION rc_guard_open_tenant_account_money_write();

        CREATE CONSTRAINT TRIGGER trg_security_deposit_entry_validate
        AFTER INSERT ON "SecurityDepositEntries"
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION rc_validate_security_deposit_entry();
        """;

    private const string CreatePaymentAttemptIntentTriggers = """
        CREATE TRIGGER trg_tenant_payment_attempt_legacy_intent_insert
        BEFORE INSERT ON "TenantPaymentAttempts"
        FOR EACH ROW EXECUTE FUNCTION rc_enforce_payment_attempt_intent();

        CREATE TRIGGER trg_tenant_payment_attempt_charge_target_immutable
        BEFORE UPDATE OF "ChargeLedgerEntryId", "AttemptType" ON "TenantPaymentAttempts"
        FOR EACH ROW EXECUTE FUNCTION rc_enforce_payment_attempt_intent();
        """;

    private const string DropTriggers = """
        DROP TRIGGER IF EXISTS trg_security_deposit_entry_validate ON "SecurityDepositEntries";
        DROP TRIGGER IF EXISTS trg_security_deposit_entry_open_account ON "SecurityDepositEntries";
        DROP TRIGGER IF EXISTS trg_security_deposit_entry_append_only ON "SecurityDepositEntries";
        DROP TRIGGER IF EXISTS trg_security_deposit_account_validate ON "SecurityDepositAccounts";
        DROP TRIGGER IF EXISTS trg_tenant_autopay_enrollment_validate ON "TenantAutopayEnrollments";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_allocation_validate ON "TenantLedgerAllocations";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_allocation_append_only ON "TenantLedgerAllocations";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_entry_payment_attempt ON "TenantLedgerEntries";
        DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_success ON "TenantPaymentAttempts";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_entry_validate ON "TenantLedgerEntries";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_entry_open_account ON "TenantLedgerEntries";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_allocation_open_account ON "TenantLedgerAllocations";
        DROP TRIGGER IF EXISTS trg_tenant_ledger_entry_append_only ON "TenantLedgerEntries";
        DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_validate ON "TenantPaymentAttempts";
        DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_charge_target_immutable ON "TenantPaymentAttempts";
        DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_legacy_intent_insert ON "TenantPaymentAttempts";
        DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write ON "TenantPaymentAttempts";
        DROP TRIGGER IF EXISTS trg_tenant_account_condition_period_guard ON "TenantAccountConditionPeriods";
        DROP TRIGGER IF EXISTS trg_lease_management_account_close ON "LeaseManagements";
        DROP TRIGGER IF EXISTS trg_tenant_account_close ON "TenantAccounts";
        DROP TRIGGER IF EXISTS trg_tenant_account_currency ON "TenantAccounts";
        """;

    private const string DropFunctions = """
        DROP FUNCTION IF EXISTS rc_validate_security_deposit_entry();
        DROP FUNCTION IF EXISTS rc_validate_security_deposit_account();
        DROP FUNCTION IF EXISTS rc_validate_tenant_autopay_enrollment();
        DROP FUNCTION IF EXISTS rc_validate_tenant_ledger_allocation();
        DROP FUNCTION IF EXISTS rc_validate_tenant_payment_attempt_success();
        DROP FUNCTION IF EXISTS rc_validate_tenant_ledger_entry();
        DROP FUNCTION IF EXISTS rc_validate_tenant_payment_attempt();
        DROP FUNCTION IF EXISTS rc_assert_provider_payment_fence(bigint, integer, integer, uuid);
        DROP FUNCTION IF EXISTS rc_schedule_tenant_payment_reconciliation(bigint, integer, integer, varchar, varchar, uuid, timestamp with time zone, varchar, varchar);
        DROP FUNCTION IF EXISTS rc_guard_open_tenant_account_money_write();
        DROP FUNCTION IF EXISTS rc_transition_tenant_payment_attempt(bigint, integer, integer, uuid, varchar, varchar, varchar, varchar, timestamp with time zone, uuid);
        DROP FUNCTION IF EXISTS rc_transition_tenant_payment_attempt(bigint, integer, integer, uuid, varchar, varchar, varchar, varchar, timestamp with time zone);
        DROP FUNCTION IF EXISTS rc_claim_exact_tenant_payment_attempt(bigint, integer, integer, varchar, interval);
        DROP FUNCTION IF EXISTS rc_claim_tenant_payment_attempt(bigint, integer, integer, varchar, interval);
        DROP FUNCTION IF EXISTS rc_enforce_payment_attempt_intent();
        DROP FUNCTION IF EXISTS rc_guard_tenant_payment_attempt_write();
        DROP FUNCTION IF EXISTS rc_guard_tenant_account_condition_period();
        DROP FUNCTION IF EXISTS rc_validate_tenant_account_close();
        DROP FUNCTION IF EXISTS rc_validate_tenant_account_currency();
        DROP FUNCTION IF EXISTS rc_reject_append_only_mutation();
        """;
}
