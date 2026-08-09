using System.Text.Json;
using FluentAssertions;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers <see cref="AuditDiffBuilder"/>: the sanitized, landlord-safe field-level diff that powers the
/// per-record History card. Verifies that Updated rows produce humanized old→new changes, Created/Deleted
/// rows produce set/was snapshots, plumbing fields are suppressed, and values are formatted (no raw JSON leaks).
/// </summary>
public sealed class AuditDiffBuilderTests
{
    private readonly AuditDiffBuilder _builder = new();

    private static AtomicAuditLog Updated(string oldJson, string newJson, string entityType = "Expense") => new()
    {
        EntityType = entityType,
        EntityId = 5,
        Operation = AuditLogOperation.Updated,
        OldValues = oldJson,
        NewValues = newJson,
    };

    [Fact]
    public void Build_Updated_ProducesHumanizedFieldDiff()
    {
        var row = Updated(
            JsonSerializer.Serialize(new { Amount = 32423, Status = "Pending" }),
            JsonSerializer.Serialize(new { Amount = 23423, Status = "Paid" }));

        var changes = _builder.Build(row);

        changes.Should().HaveCount(2);
        changes.Should().ContainSingle(c => c.Field == "Amount" && c.OldValue == "32,423" && c.NewValue == "23,423");
        changes.Should().ContainSingle(c => c.Field == "Status" && c.OldValue == "Pending" && c.NewValue == "Paid");
    }

    [Fact]
    public void Build_Updated_FormatsNumericEnumAuditValuesForKnownEntityFields()
    {
        var row = Updated(
            JsonSerializer.Serialize(new { Status = (int)WorkOrderStatus.Scheduled }),
            JsonSerializer.Serialize(new { Status = (int)WorkOrderStatus.InProgress }),
            entityType: "WorkOrder");

        var changes = _builder.Build(row);

        changes.Should().ContainSingle(c => c.Field == "Status"
            && c.OldValue == "Scheduled"
            && c.NewValue == "In progress");
    }

    [Fact]
    public void Build_HumanizesPascalCaseFieldNames()
    {
        var row = Updated(
            JsonSerializer.Serialize(new { PaymentMethod = "Visa" }),
            JsonSerializer.Serialize(new { PaymentMethod = "Cash" }));

        _builder.Build(row).Should().ContainSingle(c => c.Field == "Payment method");
    }

    [Fact]
    public void Build_SuppressesPlumbingFields()
    {
        var row = Updated(
            JsonSerializer.Serialize(new { UpdatedAt = "2026-01-01T00:00:00Z", Amount = 10 }),
            JsonSerializer.Serialize(new { UpdatedAt = "2026-02-02T00:00:00Z", Amount = 20 }));

        var changes = _builder.Build(row);

        changes.Should().ContainSingle();
        changes.Should().OnlyContain(c => c.Field == "Amount");
    }

    [Fact]
    public void Build_FormatsNullsBooleansAndDates()
    {
        var row = Updated(
            JsonSerializer.Serialize(new { Notes = (string?)null, BillableToOwner = false, DueDate = (string?)null }),
            JsonSerializer.Serialize(new { Notes = "Late fee", BillableToOwner = true, DueDate = "2026-03-15T00:00:00Z" }));

        var changes = _builder.Build(row);

        changes.Should().ContainSingle(c => c.Field == "Notes" && c.OldValue == "—" && c.NewValue == "Late fee");
        changes.Should().ContainSingle(c => c.Field == "Billable to owner" && c.OldValue == "No" && c.NewValue == "Yes");
        changes.Should().ContainSingle(c => c.Field == "Due date" && c.OldValue == "—" && c.NewValue == "Mar 15, 2026");
    }

    [Fact]
    public void Build_CreatedAndDeleted_ProducesHumanizedSnapshotLines()
    {
        var created = new AtomicAuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Created,
            NewValues = JsonSerializer.Serialize(new { Amount = 50 }),
        };
        var deleted = new AtomicAuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Deleted,
            OldValues = JsonSerializer.Serialize(new { Amount = 50 }),
        };

        _builder.Build(created).Should().ContainSingle(c =>
            c.Field == "Amount" && c.OldValue == "—" && c.NewValue == "50");
        _builder.Build(deleted).Should().ContainSingle(c =>
            c.Field == "Amount" && c.OldValue == "50" && c.NewValue == "—");
    }

    [Fact]
    public void Build_CreatedAndDeleted_SkipsNullSnapshotValues()
    {
        var created = new AtomicAuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Created,
            NewValues = "{\"Amount\":50,\"Notes\":null}",
        };
        var deleted = new AtomicAuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Deleted,
            OldValues = "{\"Amount\":50,\"Notes\":null}",
        };

        _builder.Build(created).Should().ContainSingle(c => c.Field == "Amount");
        _builder.Build(deleted).Should().ContainSingle(c => c.Field == "Amount");
    }

    [Fact]
    public void Build_CreatedSnapshot_FailsClosedForUnknownAndSensitiveFields()
    {
        var row = new AtomicAuditLog
        {
            EntityType = "OwnerEntity",
            Operation = AuditLogOperation.Created,
            NewValues = """
                {
                  "Name": "Oak Street Holdings",
                  "TaxId": "12-3456789",
                  "SSN": "111-22-3333",
                  "DateOfBirth": "1980-01-01",
                  "PasswordHash": "hash",
                  "Secret": "value",
                  "ApiKey": "key",
                  "RoutingNumber": "021000021",
                  "AccountNumber": "12345",
                  "InternalOnly": "do not show"
                }
                """,
        };

        var changes = _builder.Build(row);

        changes.Should().ContainSingle(c => c.Field == "Name");
        changes.Select(c => c.Field).Should().NotContain("Tax id");
        changes.Select(c => c.Field).Should().NotContain("SSN");
        changes.Select(c => c.Field).Should().NotContain("Date of birth");
        changes.Select(c => c.Field).Should().NotContain("Password hash");
        changes.Select(c => c.Field).Should().NotContain("Secret");
        changes.Select(c => c.Field).Should().NotContain("Api key");
        changes.Select(c => c.Field).Should().NotContain("Routing number");
        changes.Select(c => c.Field).Should().NotContain("Account number");
        changes.Select(c => c.Field).Should().NotContain("Internal only");

        var deleted = new AtomicAuditLog
        {
            EntityType = row.EntityType,
            Operation = AuditLogOperation.Deleted,
            OldValues = row.NewValues,
        };

        var deletedChanges = _builder.Build(deleted);
        deletedChanges.Should().ContainSingle(c => c.Field == "Name");
        deletedChanges.Select(c => c.Field).Should().NotContain("Tax id");
        deletedChanges.Select(c => c.Field).Should().NotContain("Account number");
        deletedChanges.Select(c => c.Field).Should().NotContain("Internal only");
    }

    [Fact]
    public void Build_RentalApplicationSnapshot_SuppressesContactExtractionAndConsentIp()
    {
        var row = new AtomicAuditLog
        {
            EntityType = "RentalApplication",
            Operation = AuditLogOperation.Created,
            NewValues = """
                {
                  "Status": "Submitted",
                  "DesiredMoveInDate": "2026-09-01",
                  "FirstName": "Avery",
                  "LastName": "Applicant",
                  "Email": "avery@example.com",
                  "Phone": "555-0100",
                  "CurrentAddress": "1 Main Street",
                  "DateOfBirth": "1990-01-01",
                  "DOB": "1990-01-01",
                  "IdExtractedFields": "{\"Email\":{\"value\":\"avery@example.com\"}}",
                  "PayStubJson": "{\"gross\":9000}",
                  "ConsentIpAddress": "203.0.113.44"
                }
                """,
        };

        var changes = _builder.Build(row);

        changes.Should().Contain(c => c.Field == "Status");
        changes.Should().Contain(c => c.Field == "Desired move in date");
        changes.Select(c => c.Field).Should().NotContain("First name");
        changes.Select(c => c.Field).Should().NotContain("Last name");
        changes.Select(c => c.Field).Should().NotContain("Email");
        changes.Select(c => c.Field).Should().NotContain("Phone");
        changes.Select(c => c.Field).Should().NotContain("Current address");
        changes.Select(c => c.Field).Should().NotContain("Date of birth");
        changes.Select(c => c.Field).Should().NotContain("DOB");
        changes.Select(c => c.Field).Should().NotContain("Id extracted fields");
        changes.Select(c => c.Field).Should().NotContain("Pay stub json");
        changes.Select(c => c.Field).Should().NotContain("Consent ip address");
    }

    [Fact]
    public void Build_TenantAccountSnapshot_SuppressesAccountNumber()
    {
        var row = new AtomicAuditLog
        {
            EntityType = "TenantAccount",
            Operation = AuditLogOperation.Created,
            NewValues = """
                {
                  "Currency": "USD",
                  "AccountNumber": "TA-0001",
                  "OpenedAtUtc": "2026-08-09T00:00:00Z",
                  "CloseReasonCode": "MovedOut"
                }
                """,
        };

        var changes = _builder.Build(row);

        changes.Should().Contain(c => c.Field == "Currency");
        changes.Should().Contain(c => c.Field == "Opened at utc");
        changes.Should().Contain(c => c.Field == "Close reason code");
        changes.Should().NotContain(c => c.Field == "Account number");
    }

    [Fact]
    public void Build_CreatedSnapshot_WithNullLegacyPayload_ReturnsEmpty()
    {
        var row = new AtomicAuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Created,
            OldValues = null,
            NewValues = null,
        };

        _builder.Build(row).Should().BeEmpty();
    }

    [Fact]
    public void Build_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        _builder.Build(Updated("{not json", "also not json")).Should().BeEmpty();
    }
}
