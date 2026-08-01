using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Migrations;

namespace RentalCommand.Data.Tests;

public sealed class ProviderPaymentIntentMigrationTests
{
    [Fact]
    public void InitialCreate_DoesNotInstallExactChargeTargetGuardsBeforeColumnExists()
    {
        var migration = new InitialCreate();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(InitialCreate)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));

        sql.Should().NotContain("\"ChargeLedgerEntryId\"");
        sql.Should().NotContain("rc_enforce_payment_attempt_intent()");
        sql.Should().NotContain("trg_tenant_payment_attempt_charge_target_immutable");
        sql.Should().NotContain("trg_tenant_payment_attempt_legacy_intent_insert");
    }

    [Fact]
    public void Migration_BackfillsOnlyExactEncodedTargets_AndClassifiesAmbiguousRowsAsLegacy()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));

        sql.Should().Contain("'^(intent|checkout|autopay):tenant-charge:[0-9]+$'");
        sql.Should().Contain("split_part(attempt.\"IdempotencyKey\", ':', 3)");
        sql.Should().Contain("SET \"AttemptType\" = 'LegacyTargetlessCharge'");
        sql.Should().NotContain("\"TenantLedgerAllocations\"",
            "allocation history does not prove which charge the payer selected");
        sql.Should().NotContain("ORDER BY candidate");
    }

    [Fact]
    public void Migration_EnforcesExactChargeTargetAndRetiresLegacyWriterShape()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var targetConstraint = builder.Operations
            .OfType<AddCheckConstraintOperation>()
            .Single(operation => operation.Name == "CK_TenantPaymentAttempt_ChargeTarget");
        targetConstraint.Sql.Should().Contain(
            "\"AttemptType\" = 'Charge' AND \"ChargeLedgerEntryId\" IS NOT NULL");

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));
        sql.Should().Contain("TG_OP = 'INSERT' AND NEW.\"AttemptType\" = 'LegacyTargetlessCharge'");
        sql.Should().Contain("LegacyTargetlessCharge is retired historical data");
        sql.Should().Contain(
            "CREATE TRIGGER trg_tenant_payment_attempt_legacy_intent_insert");
        sql.Should().Contain(
            "CREATE TRIGGER trg_tenant_payment_attempt_charge_target_immutable");
    }

    [Fact]
    public void PostgreSqlContract_ValidatesEveryCurrentReceiptType_WithoutRevivingLegacyWrites()
    {
        var sql = string.Join(
            Environment.NewLine,
            TenantAccountPostgreSqlContract.CreateStatements);
        var dropSql = string.Join(
            Environment.NewLine,
            TenantAccountPostgreSqlContract.DropStatements);

        sql.Should().MatchRegex(
            """attempt\."AttemptType" IN \(\s*'Charge',\s*'UnappliedReceipt',\s*'DepositReceipt',\s*'ImportedReceipt'\)""");
        sql.Should().MatchRegex(
            """attempt\."AttemptType" IN \(\s*'Charge',\s*'UnappliedReceipt',\s*'DepositReceipt',\s*'ImportedReceipt',\s*'Refund'\)""");
        sql.Should().Contain("attempt.\"AttemptType\" = 'LegacyTargetlessCharge'");
        sql.Should().Contain("LegacyTargetlessCharge is retired historical data");
        sql.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_enforce_payment_attempt_intent()");
        sql.Should().Contain(
            "CREATE TRIGGER trg_tenant_payment_attempt_legacy_intent_insert");
        sql.Should().Contain(
            "CREATE TRIGGER trg_tenant_payment_attempt_charge_target_immutable");
        dropSql.Should().Contain(
            "DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_legacy_intent_insert");
        dropSql.Should().Contain(
            "DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_charge_target_immutable");
        dropSql.IndexOf(
                "DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_legacy_intent_insert",
                StringComparison.Ordinal)
            .Should().BeLessThan(
                dropSql.IndexOf(
                    "DROP FUNCTION IF EXISTS rc_enforce_payment_attempt_intent()",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_ReplacesReceiptValidatorForExistingDatabases()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));

        sql.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_validate_tenant_payment_attempt_success()");
        sql.Should().Contain("'UnappliedReceipt'");
        sql.Should().Contain("'DepositReceipt'");
        sql.Should().Contain("'ImportedReceipt'");
        sql.Should().Contain("ELSIF attempt.\"AttemptType\" = 'LegacyTargetlessCharge'");
        sql.IndexOf(
                "CREATE OR REPLACE FUNCTION rc_validate_tenant_payment_attempt_success()",
                StringComparison.Ordinal)
            .Should().BeLessThan(
                sql.IndexOf(
                    "SET \"AttemptType\" = 'LegacyTargetlessCharge'",
                    StringComparison.Ordinal),
                "deferred validation of historical rows must use the legacy-aware function");
    }

    [Fact]
    public void Migration_BackfillUsesTransactionLocalExactGuard_ThenRemovesIt()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));

        sql.Should().Contain(
            "CREATE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()");
        sql.Should().Contain(
            "'rental_command.tenant_payment_attempt_exact_target_migration'");
        sql.Should().MatchRegex(
            @"set_config\(\s*'rental_command\.tenant_payment_attempt_exact_target_migration'," +
            @"\s*'on',\s*true\)");
        sql.Should().MatchRegex(
            @"set_config\(\s*'rental_command\.tenant_payment_attempt_exact_target_migration'," +
            @"\s*'off',\s*true\)");
        sql.Should().Contain(
            "EXECUTE FUNCTION rc_guard_tenant_payment_attempt_write()");
        sql.Should().Contain(
            "DROP FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()");
        sql.Should().Contain(
            "DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write");
        sql.Should().Contain(
            "to_regprocedure('rc_guard_tenant_payment_attempt_write()') IS NOT NULL");
        sql.Should().Contain(
            "tgfoid =");
        sql.Should().Contain(
            "DO $restore_guard$");
        sql.Should().NotContain("DISABLE TRIGGER");
    }

    [Fact]
    public void Migration_FlushesDeferredBackfillValidationBeforeAlteringAttemptTable()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var typeConstraintOperation = builder.Operations
            .Select((operation, index) => new { operation, index })
            .Single(item =>
                item.operation is AddCheckConstraintOperation constraint &&
                constraint.Name == "CK_TenantPaymentAttempt_Type");
        var boundarySql = builder.Operations[typeConstraintOperation.index - 1]
            .Should().BeOfType<SqlOperation>().Subject.Sql;

        boundarySql.Should().Contain("SET CONSTRAINTS ALL IMMEDIATE;");
        boundarySql.Should().Contain("SET CONSTRAINTS ALL DEFERRED;");
        boundarySql.IndexOf("SET CONSTRAINTS ALL IMMEDIATE;", StringComparison.Ordinal)
            .Should().BeLessThan(
                boundarySql.IndexOf("SET CONSTRAINTS ALL DEFERRED;", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationDown_UsesConditionalTransactionLocalExactGuard_ThenRemovesIt()
    {
        var migration = new AddProviderPaymentExactChargeTarget();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddProviderPaymentExactChargeTarget)
            .GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));

        sql.Should().Contain(
            "CREATE FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()");
        sql.Should().Contain(
            "TenantPaymentAttempt rollback rewrite is not an exact supported shape");
        sql.Should().MatchRegex(
            @"set_config\(\s*'rental_command\.tenant_payment_attempt_exact_target_migration'," +
            @"\s*'on',\s*true\)");
        sql.Should().MatchRegex(
            @"set_config\(\s*'rental_command\.tenant_payment_attempt_exact_target_migration'," +
            @"\s*'off',\s*true\)");
        sql.Should().Contain(
            "DROP TRIGGER IF EXISTS trg_tenant_payment_attempt_write");
        sql.Should().Contain(
            "to_regprocedure('rc_guard_tenant_payment_attempt_write()') IS NOT NULL");
        sql.Should().Contain(
            "tgfoid =");
        sql.Should().Contain(
            "EXECUTE FUNCTION rc_guard_tenant_payment_attempt_write()");
        sql.Should().Contain(
            "DROP FUNCTION rc_guard_tenant_payment_attempt_exact_target_migration()");
        sql.Should().NotContain("DISABLE TRIGGER");
    }
}
