using System.Text.Json;
using FluentAssertions;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers <see cref="AuditDiffBuilder"/>: the sanitized, landlord-safe field-level diff that powers the
/// per-record History card. Verifies that Updated rows produce humanized old→new changes, Created/Deleted
/// rows produce none, plumbing fields are suppressed, and values are formatted (no raw JSON leaks).
/// </summary>
public sealed class AuditDiffBuilderTests
{
    private readonly AuditDiffBuilder _builder = new();

    private static AuditLog Updated(string oldJson, string newJson) => new()
    {
        EntityType = "Expense",
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
    public void Build_CreatedAndDeleted_ReturnEmpty()
    {
        var created = new AuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Created,
            NewValues = JsonSerializer.Serialize(new { Amount = 50 }),
        };
        var deleted = new AuditLog
        {
            EntityType = "Expense",
            Operation = AuditLogOperation.Deleted,
            OldValues = JsonSerializer.Serialize(new { Amount = 50 }),
        };

        _builder.Build(created).Should().BeEmpty();
        _builder.Build(deleted).Should().BeEmpty();
    }

    [Fact]
    public void Build_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        _builder.Build(Updated("{not json", "also not json")).Should().BeEmpty();
    }
}
