using FluentAssertions;
using RentalCommand.Api.Scanning;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Automation;

public class ScanProcessingWorkerSchemaTests
{
    [Fact]
    public void ChooseExtractionSchema_WorkOrderTarget_UsesMaintenanceSchema()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("WorkOrder");

        schema.Instructions.Should().Be(WorkOrderExtractionSchema.Instructions);
        schema.Fields.Select(f => f.Name).Should().Contain([
            "property_id",
            "title",
            "description",
            "priority",
            "category",
            "estimated_cost",
        ]);
    }

    [Fact]
    public void ChooseExtractionSchema_WorkOrderTarget_AllowsGroundedLeaseIds()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("WorkOrder");
        var leaseField = schema.Fields.Single(f => f.Name == "lease_id");

        schema.Instructions.Should().Contain("leases");
        schema.Instructions.Should().Contain("lease_id");
        leaseField.Description.Should().Contain("leases[].id");
        leaseField.Description.Should().NotContain("Leave empty");
    }

    [Fact]
    public void ChooseExtractionSchema_ExpenseTarget_UsesReceiptSchema()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("Expense");

        schema.Instructions.Should().Be(ReceiptExtractionSchema.Instructions);
        schema.Fields.Select(f => f.Name).Should().Contain([
            "document_kind",
            "property_id",
            "unit_id",
        ]);
        schema.Instructions.Should().Contain("grounding context");
    }

    [Fact]
    public void ChooseExtractionSchema_LeaseTarget_UsesLeaseSchema()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("Lease");

        schema.Instructions.Should().Be(LeaseExtractionSchema.Instructions);
        schema.Fields.Select(f => f.Name).Should().Contain([
            "tenant_name",
            "property_id",
            "unit_id",
            "start_date",
            "end_date",
            "monthly_rent",
            "security_deposit",
            "late_fee",
            "rent_due_day",
        ]);
    }

    [Fact]
    public void ChooseExtractionSchema_LoanTarget_UsesLoanSchema()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("Loan");

        schema.Instructions.Should().Be(LoanExtractionSchema.Instructions);
        schema.Fields.Select(f => f.Name).Should().Contain([
            "lender",
            "original_amount",
            "current_balance",
            "annual_interest_rate_pct",
            "term_months",
            "start_date",
            "day_of_month_due",
            "monthly_principal_interest",
            "monthly_escrow",
            "property_id",
        ]);
    }
}
