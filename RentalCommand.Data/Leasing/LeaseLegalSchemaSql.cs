namespace RentalCommand.Data.Leasing;

/// <summary>
/// PostgreSQL-only invariants that EF Core cannot express in the relational model.
/// The clean baseline migration executes these blocks after creating the mapped tables.
/// </summary>
internal static class LeaseLegalSchemaSql
{
    /// <summary>Creation order for a migration whose relational tables already exist.</summary>
    public static IReadOnlyList<string> CreateStatements { get; } =
    [
        MakeLineageForeignKeysDeferrable,
        CreateExclusionsAndFunctionalIndexes,
        CreateVersionTriggers,
        CreateArtifactAndAgreementProtection,
        CreateChildFreezeAndSignerValidators,
        CreateSignatureAuditAppendOnly,
    ];

    /// <summary>Drop order before the migration removes the relational tables.</summary>
    public static IReadOnlyList<string> DropStatements { get; } =
    [
        DropSignatureAuditAppendOnly,
        DropChildFreezeAndSignerValidators,
        DropArtifactAndAgreementProtection,
        DropVersionTriggers,
        DropExclusionsAndFunctionalIndexes,
    ];

    public const string CreateExclusionsAndFunctionalIndexes = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;

        ALTER TABLE "LeaseManagements"
          ADD CONSTRAINT "EX_LeaseManagements_UnitPossession"
          EXCLUDE USING gist
          (
            "UnitId" WITH =,
            tstzrange("PossessionGivenAtUtc", "PossessionReturnedAtUtc", '[)') WITH &&
          )
          WHERE ("PossessionGivenAtUtc" IS NOT NULL AND "CanceledAtUtc" IS NULL)
          DEFERRABLE INITIALLY IMMEDIATE;

        ALTER TABLE "LeaseManagementParties"
          ADD CONSTRAINT "EX_LeaseManagementParties_Membership"
          EXCLUDE USING gist
          (
            "LeaseManagementId" WITH =,
            "TenantId" WITH =,
            daterange(
              "EffectiveFrom",
              COALESCE("EffectiveThrough" + 1, 'infinity'::date),
              '[)') WITH &&
          )
          DEFERRABLE INITIALLY IMMEDIATE;

        ALTER TABLE "LeaseManagementParties"
          ADD CONSTRAINT "EX_LeaseManagementParties_PrimaryTenant"
          EXCLUDE USING gist
          (
            "LeaseManagementId" WITH =,
            daterange(
              "EffectiveFrom",
              COALESCE("EffectiveThrough" + 1, 'infinity'::date),
              '[)') WITH &&
          )
          WHERE ("Role" = 'PrimaryTenant')
          DEFERRABLE INITIALLY IMMEDIATE;

        ALTER TABLE "LeaseAgreements"
          ADD CONSTRAINT "EX_LeaseAgreements_GoverningPeriod"
          EXCLUDE USING gist
          (
            "LeaseManagementId" WITH =,
            daterange(
              "GoverningFromOn",
              LEAST(
                COALESCE("TermEndOn" + 1, 'infinity'::date),
                COALESCE("SupersededEffectiveOn", 'infinity'::date)),
              '[)') WITH &&
          )
          WHERE ("FullyExecutedAtUtc" IS NOT NULL AND "VoidedAtUtc" IS NULL)
          DEFERRABLE INITIALLY IMMEDIATE;

        ALTER TABLE "TenantAccountConditionPeriods"
          ADD CONSTRAINT "EX_TenantAccountConditionPeriods_ConditionPeriod"
          EXCLUDE USING gist
          (
            "TenantAccountId" WITH =,
            "Condition" WITH =,
            tstzrange("StartedAtUtc", "EndedAtUtc", '[)') WITH &&
          )
          DEFERRABLE INITIALLY IMMEDIATE;

        CREATE UNIQUE INDEX "IX_LeaseAgreementSigners_LeaseAgreementId_EmailSnapshot_CI"
          ON "LeaseAgreementSigners" ("LeaseAgreementId", lower("EmailSnapshot"));

        CREATE UNIQUE INDEX "IX_LeaseAddendumSigners_LeaseAddendumId_EmailSnapshot_CI"
          ON "LeaseAddendumSigners" ("LeaseAddendumId", lower("EmailSnapshot"));
        """;

    public const string DropExclusionsAndFunctionalIndexes = """
        DROP INDEX IF EXISTS "IX_LeaseAddendumSigners_LeaseAddendumId_EmailSnapshot_CI";
        DROP INDEX IF EXISTS "IX_LeaseAgreementSigners_LeaseAgreementId_EmailSnapshot_CI";
        ALTER TABLE IF EXISTS "TenantAccountConditionPeriods"
          DROP CONSTRAINT IF EXISTS "EX_TenantAccountConditionPeriods_ConditionPeriod";
        ALTER TABLE IF EXISTS "LeaseAgreements"
          DROP CONSTRAINT IF EXISTS "EX_LeaseAgreements_GoverningPeriod";
        ALTER TABLE IF EXISTS "LeaseManagementParties"
          DROP CONSTRAINT IF EXISTS "EX_LeaseManagementParties_PrimaryTenant";
        ALTER TABLE IF EXISTS "LeaseManagementParties"
          DROP CONSTRAINT IF EXISTS "EX_LeaseManagementParties_Membership";
        ALTER TABLE IF EXISTS "LeaseManagements"
          DROP CONSTRAINT IF EXISTS "EX_LeaseManagements_UnitPossession";
        """;

    public const string MakeLineageForeignKeysDeferrable = """
        CREATE OR REPLACE FUNCTION rc_set_fk_deferrability(
          target_table regclass,
          target_columns text[],
          should_be_deferrable boolean)
        RETURNS void
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          matching_constraint text;
          matching_count integer;
        BEGIN
          SELECT min(candidate.conname), count(*)::integer
          INTO matching_constraint, matching_count
          FROM (
            SELECT constraint_row.conname
            FROM pg_constraint AS constraint_row
            WHERE constraint_row.conrelid = target_table
              AND constraint_row.contype = 'f'
              AND (
                SELECT array_agg(attribute_row.attname::text ORDER BY key_column.ordinality)
                FROM unnest(constraint_row.conkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                JOIN pg_attribute AS attribute_row
                  ON attribute_row.attrelid = constraint_row.conrelid
                 AND attribute_row.attnum = key_column.attnum
              ) = target_columns
          ) AS candidate;

          IF matching_count <> 1 THEN
            RAISE EXCEPTION
              'Expected exactly one foreign key on table % for columns %, found %',
              target_table, target_columns, matching_count;
          END IF;

          EXECUTE format(
            'ALTER TABLE %s ALTER CONSTRAINT %I %s',
            target_table,
            matching_constraint,
            CASE WHEN should_be_deferrable
              THEN 'DEFERRABLE INITIALLY IMMEDIATE'
              ELSE 'NOT DEFERRABLE'
            END);
        END;
        $function$;

        SELECT rc_set_fk_deferrability(
          '"LeaseManagements"'::regclass,
          ARRAY['TransferredFromLeaseManagementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAgreements"'::regclass,
          ARRAY['ReplacesAgreementId', 'LeaseManagementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAgreements"'::regclass,
          ARRAY['TransferredFromAgreementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAgreements"'::regclass,
          ARRAY['RenewsAgreementId', 'LeaseManagementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAgreements"'::regclass,
          ARRAY['SupersededByAgreementId', 'LeaseManagementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAddenda"'::regclass,
          ARRAY['ReplacesAddendumId', 'SeriesPublicId', 'LeaseManagementId', 'PortfolioId'],
          true);
        SELECT rc_set_fk_deferrability(
          '"LeaseAddenda"'::regclass,
          ARRAY['SupersededByAddendumId', 'SeriesPublicId', 'LeaseManagementId', 'PortfolioId'],
          true);

        DROP FUNCTION rc_set_fk_deferrability(regclass, text[], boolean);
        """;

    public const string MakeLineageForeignKeysNotDeferrable = """
        CREATE OR REPLACE FUNCTION rc_set_fk_deferrability(
          target_table regclass,
          target_columns text[],
          should_be_deferrable boolean)
        RETURNS void
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          matching_constraint text;
          matching_count integer;
        BEGIN
          SELECT min(candidate.conname), count(*)::integer
          INTO matching_constraint, matching_count
          FROM (
            SELECT constraint_row.conname
            FROM pg_constraint AS constraint_row
            WHERE constraint_row.conrelid = target_table
              AND constraint_row.contype = 'f'
              AND (
                SELECT array_agg(attribute_row.attname::text ORDER BY key_column.ordinality)
                FROM unnest(constraint_row.conkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                JOIN pg_attribute AS attribute_row
                  ON attribute_row.attrelid = constraint_row.conrelid
                 AND attribute_row.attnum = key_column.attnum
              ) = target_columns
          ) AS candidate;

          IF matching_count <> 1 THEN
            RAISE EXCEPTION
              'Expected exactly one foreign key on table % for columns %, found %',
              target_table, target_columns, matching_count;
          END IF;

          EXECUTE format(
            'ALTER TABLE %s ALTER CONSTRAINT %I %s',
            target_table,
            matching_constraint,
            CASE WHEN should_be_deferrable
              THEN 'DEFERRABLE INITIALLY IMMEDIATE'
              ELSE 'NOT DEFERRABLE'
            END);
        END;
        $function$;

        SELECT rc_set_fk_deferrability('"LeaseAddenda"'::regclass,
          ARRAY['SupersededByAddendumId', 'SeriesPublicId', 'LeaseManagementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseAddenda"'::regclass,
          ARRAY['ReplacesAddendumId', 'SeriesPublicId', 'LeaseManagementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseAgreements"'::regclass,
          ARRAY['SupersededByAgreementId', 'LeaseManagementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseAgreements"'::regclass,
          ARRAY['RenewsAgreementId', 'LeaseManagementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseAgreements"'::regclass,
          ARRAY['TransferredFromAgreementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseAgreements"'::regclass,
          ARRAY['ReplacesAgreementId', 'LeaseManagementId', 'PortfolioId'], false);
        SELECT rc_set_fk_deferrability('"LeaseManagements"'::regclass,
          ARRAY['TransferredFromLeaseManagementId', 'PortfolioId'], false);

        DROP FUNCTION rc_set_fk_deferrability(regclass, text[], boolean);
        """;

    public const string CreateVersionTriggers = """
        CREATE OR REPLACE FUNCTION rc_validate_lease_agreement_version()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          expected_version integer;
        BEGIN
          PERFORM 1
          FROM "LeaseManagements"
          WHERE "Id" = NEW."LeaseManagementId"
            AND "PortfolioId" = NEW."PortfolioId"
          FOR UPDATE;

          IF NOT FOUND THEN
            RAISE EXCEPTION 'LeaseManagement % is not in portfolio %',
              NEW."LeaseManagementId", NEW."PortfolioId";
          END IF;

          SELECT COALESCE(MAX(agreement."VersionNumber"), 0) + 1
          INTO expected_version
          FROM "LeaseAgreements" AS agreement
          WHERE agreement."LeaseManagementId" = NEW."LeaseManagementId"
            AND agreement."PortfolioId" = NEW."PortfolioId";

          IF NEW."VersionNumber" <> expected_version THEN
            RAISE EXCEPTION 'LeaseAgreement version must be %, received %',
              expected_version, NEW."VersionNumber";
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAgreements_ValidateVersion"
          BEFORE INSERT ON "LeaseAgreements"
          FOR EACH ROW EXECUTE FUNCTION rc_validate_lease_agreement_version();

        CREATE OR REPLACE FUNCTION rc_validate_lease_addendum_version()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          expected_version integer;
        BEGIN
          PERFORM 1
          FROM "LeaseManagements"
          WHERE "Id" = NEW."LeaseManagementId"
            AND "PortfolioId" = NEW."PortfolioId"
          FOR UPDATE;

          IF NOT FOUND THEN
            RAISE EXCEPTION 'LeaseManagement % is not in portfolio %',
              NEW."LeaseManagementId", NEW."PortfolioId";
          END IF;

          SELECT COALESCE(MAX(addendum."VersionNumber"), 0) + 1
          INTO expected_version
          FROM "LeaseAddenda" AS addendum
          WHERE addendum."LeaseManagementId" = NEW."LeaseManagementId"
            AND addendum."PortfolioId" = NEW."PortfolioId"
            AND addendum."SeriesPublicId" = NEW."SeriesPublicId";

          IF NEW."VersionNumber" <> expected_version THEN
            RAISE EXCEPTION 'LeaseAddendum version must be %, received %',
              expected_version, NEW."VersionNumber";
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAddenda_ValidateVersion"
          BEFORE INSERT ON "LeaseAddenda"
          FOR EACH ROW EXECUTE FUNCTION rc_validate_lease_addendum_version();
        """;

    public const string DropVersionTriggers = """
        DROP TRIGGER IF EXISTS "TR_LeaseAddenda_ValidateVersion" ON "LeaseAddenda";
        DROP FUNCTION IF EXISTS rc_validate_lease_addendum_version();
        DROP TRIGGER IF EXISTS "TR_LeaseAgreements_ValidateVersion" ON "LeaseAgreements";
        DROP FUNCTION IF EXISTS rc_validate_lease_agreement_version();
        """;

    public const string CreateArtifactAndAgreementProtection = """
        CREATE OR REPLACE FUNCTION rc_reject_legal_artifact_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          RAISE EXCEPTION 'LegalDocumentArtifacts are immutable';
        END;
        $function$;

        CREATE TRIGGER "TR_LegalDocumentArtifacts_Immutable"
          BEFORE UPDATE OR DELETE ON "LegalDocumentArtifacts"
          FOR EACH ROW EXECUTE FUNCTION rc_reject_legal_artifact_mutation();

        CREATE OR REPLACE FUNCTION rc_protect_lease_agreement()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'LeaseAgreements are durable and cannot be deleted';
          END IF;

          IF OLD."IssuedAtUtc" IS NOT NULL AND ROW(
              NEW."PublicId", NEW."PortfolioId", NEW."LeaseManagementId", NEW."VersionNumber",
              NEW."AgreementNumber", NEW."ChangeType", NEW."TransferredFromAgreementId",
              NEW."ReplacesAgreementId", NEW."RenewsAgreementId", NEW."TermType",
              NEW."TermStartOn", NEW."TermEndOn", NEW."GoverningFromOn",
              NEW."BaseRentAmount", NEW."RentDueDay", NEW."SecurityDepositObligation",
              NEW."LateFeeAmount", NEW."GracePeriodDays", NEW."Currency",
              NEW."TermsSchemaVersion", NEW."TermsPayload", NEW."DocumentTemplateId",
              NEW."DocumentTemplateVersion", NEW."IssuedArtifactId", NEW."IssuedAtUtc",
              NEW."DraftCanceledAtUtc", NEW."DraftCancellationReason",
              NEW."CreatedAtUtc", NEW."CreatedByUserId", NEW."DraftRevision")
            IS DISTINCT FROM ROW(
              OLD."PublicId", OLD."PortfolioId", OLD."LeaseManagementId", OLD."VersionNumber",
              OLD."AgreementNumber", OLD."ChangeType", OLD."TransferredFromAgreementId",
              OLD."ReplacesAgreementId", OLD."RenewsAgreementId", OLD."TermType",
              OLD."TermStartOn", OLD."TermEndOn", OLD."GoverningFromOn",
              OLD."BaseRentAmount", OLD."RentDueDay", OLD."SecurityDepositObligation",
              OLD."LateFeeAmount", OLD."GracePeriodDays", OLD."Currency",
              OLD."TermsSchemaVersion", OLD."TermsPayload", OLD."DocumentTemplateId",
              OLD."DocumentTemplateVersion", OLD."IssuedArtifactId", OLD."IssuedAtUtc",
              OLD."DraftCanceledAtUtc", OLD."DraftCancellationReason",
              OLD."CreatedAtUtc", OLD."CreatedByUserId", OLD."DraftRevision") THEN
            RAISE EXCEPTION 'Issued LeaseAgreement contractual fields are immutable';
          END IF;

          IF OLD."FullyExecutedAtUtc" IS NOT NULL AND
             ROW(NEW."FullyExecutedAtUtc", NEW."ExecutedArtifactId") IS DISTINCT FROM
             ROW(OLD."FullyExecutedAtUtc", OLD."ExecutedArtifactId") THEN
            RAISE EXCEPTION 'LeaseAgreement execution evidence is immutable once recorded';
          END IF;

          IF OLD."VoidedAtUtc" IS NOT NULL AND
             ROW(NEW."VoidedAtUtc", NEW."VoidReasonCode", NEW."VoidNote") IS DISTINCT FROM
             ROW(OLD."VoidedAtUtc", OLD."VoidReasonCode", OLD."VoidNote") THEN
            RAISE EXCEPTION 'LeaseAgreement void evidence is immutable once recorded';
          END IF;

          IF OLD."SupersededEffectiveOn" IS NOT NULL AND
             ROW(NEW."SupersededEffectiveOn", NEW."SupersededByAgreementId", NEW."SupersessionRecordedAtUtc")
             IS DISTINCT FROM
             ROW(OLD."SupersededEffectiveOn", OLD."SupersededByAgreementId", OLD."SupersessionRecordedAtUtc") THEN
            RAISE EXCEPTION 'LeaseAgreement supersession evidence is immutable once recorded';
          END IF;

          IF OLD."DraftCanceledAtUtc" IS NOT NULL AND
             ROW(NEW."DraftCanceledAtUtc", NEW."DraftCancellationReason") IS DISTINCT FROM
             ROW(OLD."DraftCanceledAtUtc", OLD."DraftCancellationReason") THEN
            RAISE EXCEPTION 'LeaseAgreement draft-cancellation evidence is immutable once recorded';
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAgreements_ProtectLegalState"
          BEFORE UPDATE OR DELETE ON "LeaseAgreements"
          FOR EACH ROW EXECUTE FUNCTION rc_protect_lease_agreement();

        CREATE OR REPLACE FUNCTION rc_protect_lease_addendum()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'LeaseAddenda are durable and cannot be deleted';
          END IF;

          IF OLD."IssuedAtUtc" IS NOT NULL AND ROW(
              NEW."PublicId", NEW."SeriesPublicId", NEW."PortfolioId", NEW."LeaseManagementId",
              NEW."BaseAgreementId", NEW."VersionNumber", NEW."AddendumNumber", NEW."Purpose",
              NEW."ReplacesAddendumId", NEW."EffectiveFromOn", NEW."EffectiveThroughOn",
              NEW."TermsSchemaVersion", NEW."TermsPayload", NEW."DocumentTemplateId",
              NEW."DocumentTemplateVersion", NEW."IssuedArtifactId", NEW."IssuedAtUtc",
              NEW."DraftCanceledAtUtc", NEW."DraftCancellationReason",
              NEW."CreatedAtUtc", NEW."CreatedByUserId", NEW."DraftRevision")
            IS DISTINCT FROM ROW(
              OLD."PublicId", OLD."SeriesPublicId", OLD."PortfolioId", OLD."LeaseManagementId",
              OLD."BaseAgreementId", OLD."VersionNumber", OLD."AddendumNumber", OLD."Purpose",
              OLD."ReplacesAddendumId", OLD."EffectiveFromOn", OLD."EffectiveThroughOn",
              OLD."TermsSchemaVersion", OLD."TermsPayload", OLD."DocumentTemplateId",
              OLD."DocumentTemplateVersion", OLD."IssuedArtifactId", OLD."IssuedAtUtc",
              OLD."DraftCanceledAtUtc", OLD."DraftCancellationReason",
              OLD."CreatedAtUtc", OLD."CreatedByUserId", OLD."DraftRevision") THEN
            RAISE EXCEPTION 'Issued LeaseAddendum contractual fields are immutable';
          END IF;

          IF OLD."FullyExecutedAtUtc" IS NOT NULL AND
             ROW(NEW."FullyExecutedAtUtc", NEW."ExecutedArtifactId") IS DISTINCT FROM
             ROW(OLD."FullyExecutedAtUtc", OLD."ExecutedArtifactId") THEN
            RAISE EXCEPTION 'LeaseAddendum execution evidence is immutable once recorded';
          END IF;

          IF OLD."VoidedAtUtc" IS NOT NULL AND
             ROW(NEW."VoidedAtUtc", NEW."VoidReasonCode", NEW."VoidNote") IS DISTINCT FROM
             ROW(OLD."VoidedAtUtc", OLD."VoidReasonCode", OLD."VoidNote") THEN
            RAISE EXCEPTION 'LeaseAddendum void evidence is immutable once recorded';
          END IF;

          IF OLD."SupersededEffectiveOn" IS NOT NULL AND
             ROW(NEW."SupersededEffectiveOn", NEW."SupersededByAddendumId", NEW."SupersessionRecordedAtUtc")
             IS DISTINCT FROM
             ROW(OLD."SupersededEffectiveOn", OLD."SupersededByAddendumId", OLD."SupersessionRecordedAtUtc") THEN
            RAISE EXCEPTION 'LeaseAddendum supersession evidence is immutable once recorded';
          END IF;

          IF OLD."DraftCanceledAtUtc" IS NOT NULL AND
             ROW(NEW."DraftCanceledAtUtc", NEW."DraftCancellationReason") IS DISTINCT FROM
             ROW(OLD."DraftCanceledAtUtc", OLD."DraftCancellationReason") THEN
            RAISE EXCEPTION 'LeaseAddendum draft-cancellation evidence is immutable once recorded';
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAddenda_ProtectLegalState"
          BEFORE UPDATE OR DELETE ON "LeaseAddenda"
          FOR EACH ROW EXECUTE FUNCTION rc_protect_lease_addendum();
        """;

    public const string DropArtifactAndAgreementProtection = """
        DROP TRIGGER IF EXISTS "TR_LeaseAddenda_ProtectLegalState" ON "LeaseAddenda";
        DROP FUNCTION IF EXISTS rc_protect_lease_addendum();
        DROP TRIGGER IF EXISTS "TR_LeaseAgreements_ProtectLegalState" ON "LeaseAgreements";
        DROP FUNCTION IF EXISTS rc_protect_lease_agreement();
        DROP TRIGGER IF EXISTS "TR_LegalDocumentArtifacts_Immutable" ON "LegalDocumentArtifacts";
        DROP FUNCTION IF EXISTS rc_reject_legal_artifact_mutation();
        """;

    public const string CreateChildFreezeAndSignerValidators = """
        CREATE OR REPLACE FUNCTION rc_freeze_agreement_signer_after_issue()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          old_parent_issued timestamp with time zone;
          new_parent_issued timestamp with time zone;
        BEGIN
          IF TG_OP <> 'INSERT' THEN
            SELECT agreement."IssuedAtUtc" INTO old_parent_issued
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."Id" = OLD."LeaseAgreementId"
              AND agreement."PortfolioId" = OLD."PortfolioId";
          END IF;

          IF TG_OP <> 'DELETE' THEN
            SELECT agreement."IssuedAtUtc" INTO new_parent_issued
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."Id" = NEW."LeaseAgreementId"
              AND agreement."PortfolioId" = NEW."PortfolioId";
          END IF;

          IF old_parent_issued IS NOT NULL OR new_parent_issued IS NOT NULL THEN
            RAISE EXCEPTION 'LeaseAgreementSigners are frozen after Agreement issuance';
          END IF;

          IF TG_OP = 'DELETE' THEN
            RETURN OLD;
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAgreementSigners_FreezeAfterIssue"
          BEFORE INSERT OR UPDATE OR DELETE ON "LeaseAgreementSigners"
          FOR EACH ROW EXECUTE FUNCTION rc_freeze_agreement_signer_after_issue();

        CREATE OR REPLACE FUNCTION rc_freeze_addendum_child_after_issue()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          old_parent_id integer;
          old_portfolio_id integer;
          new_parent_id integer;
          new_portfolio_id integer;
          old_parent_issued timestamp with time zone;
          new_parent_issued timestamp with time zone;
        BEGIN
          IF TG_OP <> 'INSERT' THEN
            old_parent_id := (to_jsonb(OLD) ->> 'LeaseAddendumId')::integer;
            old_portfolio_id := (to_jsonb(OLD) ->> 'PortfolioId')::integer;
            SELECT addendum."IssuedAtUtc" INTO old_parent_issued
            FROM "LeaseAddenda" AS addendum
            WHERE addendum."Id" = old_parent_id
              AND addendum."PortfolioId" = old_portfolio_id;
          END IF;

          IF TG_OP <> 'DELETE' THEN
            new_parent_id := (to_jsonb(NEW) ->> 'LeaseAddendumId')::integer;
            new_portfolio_id := (to_jsonb(NEW) ->> 'PortfolioId')::integer;
            SELECT addendum."IssuedAtUtc" INTO new_parent_issued
            FROM "LeaseAddenda" AS addendum
            WHERE addendum."Id" = new_parent_id
              AND addendum."PortfolioId" = new_portfolio_id;
          END IF;

          IF old_parent_issued IS NOT NULL OR new_parent_issued IS NOT NULL THEN
            RAISE EXCEPTION '% rows are frozen after Addendum issuance', TG_TABLE_NAME;
          END IF;

          IF TG_OP = 'DELETE' THEN
            RETURN OLD;
          END IF;

          RETURN NEW;
        END;
        $function$;

        CREATE TRIGGER "TR_LeaseAddendumSigners_FreezeAfterIssue"
          BEFORE INSERT OR UPDATE OR DELETE ON "LeaseAddendumSigners"
          FOR EACH ROW EXECUTE FUNCTION rc_freeze_addendum_child_after_issue();

        CREATE TRIGGER "TR_LeaseAddendumFinancialEffects_FreezeAfterIssue"
          BEFORE INSERT OR UPDATE OR DELETE ON "LeaseAddendumFinancialEffects"
          FOR EACH ROW EXECUTE FUNCTION rc_freeze_addendum_child_after_issue();

        CREATE OR REPLACE FUNCTION rc_require_agreement_signer_at_issue()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF NEW."IssuedAtUtc" IS NOT NULL AND NOT EXISTS (
            SELECT 1
            FROM "LeaseAgreementSigners" AS signer
            WHERE signer."LeaseAgreementId" = NEW."Id"
              AND signer."PortfolioId" = NEW."PortfolioId"
              AND signer."IsRequired"
              AND signer."SignerRole" IN ('PrimaryTenant', 'CoTenant')
          ) THEN
            RAISE EXCEPTION 'Issued LeaseAgreement % requires a responsible tenant signer', NEW."Id";
          END IF;

          RETURN NULL;
        END;
        $function$;

        CREATE CONSTRAINT TRIGGER "TR_LeaseAgreements_RequireSignerAtIssue"
          AFTER INSERT OR UPDATE ON "LeaseAgreements"
          DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION rc_require_agreement_signer_at_issue();

        CREATE OR REPLACE FUNCTION rc_require_addendum_signer_at_issue()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          IF NEW."IssuedAtUtc" IS NOT NULL AND NOT EXISTS (
            SELECT 1
            FROM "LeaseAddendumSigners" AS signer
            WHERE signer."LeaseAddendumId" = NEW."Id"
              AND signer."PortfolioId" = NEW."PortfolioId"
              AND signer."IsRequired"
          ) THEN
            RAISE EXCEPTION 'Issued LeaseAddendum % requires a signer', NEW."Id";
          END IF;

          RETURN NULL;
        END;
        $function$;

        CREATE CONSTRAINT TRIGGER "TR_LeaseAddenda_RequireSignerAtIssue"
          AFTER INSERT OR UPDATE ON "LeaseAddenda"
          DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION rc_require_addendum_signer_at_issue();
        """;

    public const string DropChildFreezeAndSignerValidators = """
        DROP TRIGGER IF EXISTS "TR_LeaseAddenda_RequireSignerAtIssue" ON "LeaseAddenda";
        DROP FUNCTION IF EXISTS rc_require_addendum_signer_at_issue();
        DROP TRIGGER IF EXISTS "TR_LeaseAgreements_RequireSignerAtIssue" ON "LeaseAgreements";
        DROP FUNCTION IF EXISTS rc_require_agreement_signer_at_issue();
        DROP TRIGGER IF EXISTS "TR_LeaseAddendumFinancialEffects_FreezeAfterIssue"
          ON "LeaseAddendumFinancialEffects";
        DROP TRIGGER IF EXISTS "TR_LeaseAddendumSigners_FreezeAfterIssue" ON "LeaseAddendumSigners";
        DROP FUNCTION IF EXISTS rc_freeze_addendum_child_after_issue();
        DROP TRIGGER IF EXISTS "TR_LeaseAgreementSigners_FreezeAfterIssue" ON "LeaseAgreementSigners";
        DROP FUNCTION IF EXISTS rc_freeze_agreement_signer_after_issue();
        """;

    public const string CreateSignatureAuditAppendOnly = """
        CREATE OR REPLACE FUNCTION rc_reject_signature_audit_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
          RAISE EXCEPTION 'SignatureAuditEvents are append-only';
        END;
        $function$;

        CREATE TRIGGER "TR_SignatureAuditEvents_AppendOnly"
          BEFORE UPDATE OR DELETE ON "SignatureAuditEvents"
          FOR EACH ROW EXECUTE FUNCTION rc_reject_signature_audit_mutation();
        """;

    public const string DropSignatureAuditAppendOnly = """
        DROP TRIGGER IF EXISTS "TR_SignatureAuditEvents_AppendOnly" ON "SignatureAuditEvents";
        DROP FUNCTION IF EXISTS rc_reject_signature_audit_mutation();
        """;
}
