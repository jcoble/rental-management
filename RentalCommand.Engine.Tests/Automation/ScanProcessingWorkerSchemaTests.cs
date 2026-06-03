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
    public void ChooseExtractionSchema_ExpenseTarget_UsesReceiptSchema()
    {
        var schema = ScanProcessingWorker.ChooseExtractionSchema("Expense");

        schema.Instructions.Should().Be(ReceiptExtractionSchema.Instructions);
        schema.Fields.Select(f => f.Name).Should().Contain("document_kind");
    }
}
